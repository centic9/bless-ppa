// created on 6/6/2004 at 10:46 AM
/*
 *   Copyright (c) 2004, Alexandros Frantzis (alf82 [at] freemail [dot] gr)
 *
 *   This file is part of Bless.
 *
 *   Bless is free software; you can redistribute it and/or modify
 *   it under the terms of the GNU General Public License as published by
 *   the Free Software Foundation; either version 2 of the License, or
 *   (at your option) any later version.
 *
 *   Bless is distributed in the hope that it will be useful,
 *   but WITHOUT ANY WARRANTY; without even the implied warranty of
 *   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *   GNU General Public License for more details.
 *
 *   You should have received a copy of the GNU General Public License
 *   along with Bless; if not, write to the Free Software
 *   Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using System.Collections;
using System;
using System.IO;
using System.Threading;
using Bless.Util;
using Bless.Tools;

namespace Bless.Buffers {

///<summary>
/// A buffer for holding bytes in a versatile manner.
/// It supports undo-redo and can easily handle large files.
/// Editing is also very cheap.
///</summary>
public class ByteBuffer : BaseBuffer {

	///<summary>The file buffer associated with the ByteBuffer</summary>
	internal FileBuffer	fileBuf;
	
	///<summary>The collection of segments that comprise the ByteBuffer</summary>
	internal SegmentCollection segCol;
	
	///<summary>Double ended queues to hold undo/redo actions</summary>
	Deque<ByteBufferAction> undoDeque;
	Deque<ByteBufferAction> redoDeque;
	
	///<summary>The last action before a Save was made</summary>
	ByteBufferAction SaveCheckpoint;
	
	///<summary>Watches the file for external changes (outside Bless)</summary>
	FileSystemWatcher fsw;
	
	int maxUndoActions;
	bool changedBeyondUndo;
	string tempDir;
	
	// automatic file naming
	string autoFilename;
	static int autoNum = 1;
	
	internal long size;
	
	// chaining related
	bool actionChaining;
	bool actionChainingFirst;
	MultiAction multiAction;
	
	
	///<summary>
	///  Object to use for synchronized access to the ByteBuffer
	///</summary>
	readonly public object LockObj = new object();
	
	// buffer permissions
	bool readAllowed;
	bool modifyAllowed;
	bool fileOperationsAllowed;
	bool emitEvents;
	
	public delegate void ChangedHandler(ByteBuffer bb);
	
	///<summary>Emitted when buffer changes</summary>
	public event ChangedHandler Changed;
	///<summary>Emitted when file changes (outside DataView)</summary>
	public event ChangedHandler FileChanged;
	///<summary>Emitted when buffer permissions change</summary>
	public event ChangedHandler PermissionsChanged;
	
	public void EmitChanged()
	{
		if (emitEvents && Changed != null) {
			Changed(this);
		}
	}

	public void EmitFileChanged()
	{
		if (emitEvents && FileChanged != null) {
			FileChanged(this);
		}
	}
	
	public void EmitPermissionsChanged()
	{
		if (emitEvents && PermissionsChanged != null) {
			PermissionsChanged(this);
		}
	}
	
	// related to asynchronous save model
	AsyncCallback userSaveAsyncCallback;
	AutoResetEvent saveFinishedEvent;
	bool useGLibIdle;
	
	public ByteBuffer() 
	{
		segCol = new SegmentCollection();
		undoDeque = new Deque<ByteBufferAction>();
		redoDeque = new Deque<ByteBufferAction>();
		size = 0;
		SaveCheckpoint = null;
		
		// name the buffer automatically
		autoFilename = "Untitled " + ByteBuffer.autoNum;
		ByteBuffer.autoNum++;
		
		// set default permissions 
		readAllowed = true;
		fileOperationsAllowed = true;
		modifyAllowed = true;
		
		saveFinishedEvent = new AutoResetEvent(false);
		useGLibIdle = false;
		emitEvents = true;
		
		maxUndoActions = -1; // unlimited undo
		tempDir = Path.GetTempPath();
	}
	
	///<summary>Create a ByteBuffer loaded with a file</summary>
	static public ByteBuffer FromFile(string filename)
	{
		ByteBuffer bb = new ByteBuffer();
		
		bb.LoadWithFile(filename);
		
		// fix automatic file naming
		ByteBuffer.autoNum--;
		
		return bb;
	}
	
	///<summary>Create a ByteBuffer with a dummy-name</summary>
	public ByteBuffer(string filename): this() 
	{
		this.autoFilename = filename;
		
		// fix automatic file naming
		ByteBuffer.autoNum--;
	}
	
	///<summary>Regard all following actions as a single one</summary>
	public void BeginActionChaining()
	{
		actionChaining = true;
		actionChainingFirst = true;
		multiAction = new MultiAction();
		emitEvents = false;
	}
	
	///<summary>Stop regarding actions as a single one</summary>
	public void EndActionChaining()
	{
		actionChaining = false;
		actionChainingFirst = false;
		emitEvents = true;
		
		EmitChanged();
	}
	
	///<summary>Handle actions as a single one</summary>
	bool HandleChaining(ByteBufferAction action)
	{
		if (!actionChaining)
			return false;
		
		// add the multiAction to the undo deque	
		if (actionChainingFirst) {
			AddUndoAction(multiAction);
			actionChainingFirst = false;
		}
		
		multiAction.Add(action);
		
		return true;
	}
	
	///<summary>
	/// Add an action to the undo deque, taking into
	/// account maximum undo action restrictions 
	///</summary>
	void AddUndoAction(ByteBufferAction action)
	{
		if (maxUndoActions!=-1)
			while (undoDeque.Count >= maxUndoActions) {
				undoDeque.RemoveEnd();
				changedBeyondUndo = true;
			}
			
		undoDeque.AddFront(action);
	}
	
	void RedoDequeDispose()
	{
		redoDeque.Clear();
	}
	
	///<summary>Append bytes at the end of the buffer</summary>
	public override void Append(byte[] data, long index, long length) 
	{	
		lock (LockObj) {
			if (!modifyAllowed) return;
			if (!IsResizable) return;
		
			AppendAction aa = new AppendAction(data, index, length, this);	
			aa.Do();
			
			// if action isn't handled as chained (ActionChaining==false)
			// handle it manually
			if (!HandleChaining(aa)) {
				AddUndoAction(aa);
				RedoDequeDispose();
			}
			
			EmitChanged();
		}
	}

	///<summary>Insert bytes into the buffer</summary>
	public override void Insert(long pos, byte[] data, long index, long length) 
	{
		lock (LockObj) {
			if (!modifyAllowed) return;
			if (!IsResizable) return;
			
			if (pos == size) {
				Append(data, index, length);
				return;
			}
			
			InsertAction ia = new InsertAction(pos, data, index, length, this);
			ia.Do();
			
			// if action isn't handled as chained (ActionChaining == false)
			// handle it manually
			if (!HandleChaining(ia)) {		
				AddUndoAction(ia);
				RedoDequeDispose();
			}
			
			EmitChanged();
		}
	}		

	///<summary>Delete bytes from the buffer</summary>
	public void Delete(long pos1, long pos2) 
	{
		lock (LockObj) {
			if (!modifyAllowed) return;
			if (!IsResizable) return;
		
			DeleteAction da = new DeleteAction(pos1, pos2, this);
			da.Do();
			
			// if action isn't handled as chained (ActionChaining == false)
			// handle it manually
			if (!HandleChaining(da)) {	
				AddUndoAction(da);
				RedoDequeDispose();
			}
			
			EmitChanged();
		}
	}
	
	///<summary>Replace bytes in the buffer</summary>
	public void Replace(long pos1, long pos2, byte[] data, long index, long length) 
	{
		lock (LockObj) {
			if (!modifyAllowed) return;
			
			// if the file isn't resizable allow replaces 
			// only if they don't change the file length
			bool equalLength = (pos2 - pos1 + 1 == length);
			if (!IsResizable && !equalLength) return;
			
			ReplaceAction ra = new ReplaceAction(pos1, pos2, data, index, length, this);
			ra.Do();
			
			// if action isn't handled as chained (ActionChaining == false)
			// handle it manually
			if (!HandleChaining(ra)) {
				AddUndoAction(ra);
				RedoDequeDispose();
			}
			
			EmitChanged();
		}
	}
	
	public void Replace(long pos1, long pos2, byte[] data) 
	{
		Replace(pos1, pos2, data, 0, data.Length);
	}
	
	///<summary>Undo the last action</summary>
	public void Undo() 
	{
		lock (LockObj) {	
			if (!modifyAllowed) return;
		
			// if there is an action to undo
			if (undoDeque.Count > 0) {
				ByteBufferAction action = undoDeque.RemoveFront();
				action.Undo();
				redoDeque.AddFront(action);
				
				EmitChanged();
			}
			
		}
	}	
	
	///<summary>Redo the last undone action</summary>
	public void Redo() 
	{
		lock (LockObj) {	
			if (!modifyAllowed) return;
			
			// if there is an action to redo
			if (redoDeque.Count > 0) {
				ByteBufferAction action = redoDeque.RemoveFront();
				action.Do();
				AddUndoAction(action);
				
				EmitChanged();
			}
			
		}
	}
	
	///<summary>
	/// Save the buffer as a file, using an asynchronous model
	///</summary>
	public IAsyncResult BeginSaveAs(string filename, ProgressCallback progressCallback, AsyncCallback ac)
	{
		lock (LockObj) {
			if (!fileOperationsAllowed) return null;
			
			saveFinishedEvent.Reset();
			userSaveAsyncCallback = ac;
			
			SaveAsOperation so = new SaveAsOperation(this, filename, progressCallback, SaveAsAsyncCallback, useGLibIdle);
			
			// don't allow messing up with the buffer
			// while we are saving
			// ...ReadAllowed is set in SaveOperation
			// this.ReadAllowed = false;
			this.ModifyAllowed = false;
			this.FileOperationsAllowed = false;
			this.EmitEvents = false;
			if (fsw != null)
				fsw.EnableRaisingEvents = false;
			
			// start save thread
			Thread saveThread = new Thread(so.OperationThread);
			saveThread.IsBackground = true;
			saveThread.Start();
			
			return new ThreadedAsyncResult(so, saveFinishedEvent, false);
		}
		
	}
	
	///<summary>
	/// Called when an asynchronous Save As operation finishes
	///</summary>
	void SaveAsAsyncCallback(IAsyncResult ar)
	{
		lock (LockObj) {
			SaveAsOperation so = (SaveAsOperation)ar.AsyncState;
			
			// re-allow buffer usage
			this.FileOperationsAllowed = true;
			
			
			// make sure Save As went smoothly before doing anything
			if (so.Result==SaveAsOperation.OperationResult.Finished) {
				// make sure data in undo redo are stored safely
				// because we are going to close the file
				MakePrivateCopyOfUndoRedo();
				CloseFile();
				LoadWithFile(so.SavePath);
				
				if (undoDeque.Count > 0)
					SaveCheckpoint = undoDeque.PeekFront();
				else
					SaveCheckpoint = null;
					
				changedBeyondUndo = false;
			}
			else {
				// if cancelled or caught an exception
				// delete the file only if we have altered it
				if (so.StageReached != SaveAsOperation.SaveAsStage.BeforeCreate) {
					try {
						System.IO.File.Delete(so.SavePath);
					}
					catch (Exception e) {
						System.Console.WriteLine(e.Message);
					}
				}
			}
			
			// re-allow buffer usage
			this.ReadAllowed = true;
			this.ModifyAllowed = true;
			
			this.EmitEvents = true;
			
			if (fsw != null)
				fsw.EnableRaisingEvents = true;
			
			// notify the world about the changes			
			EmitPermissionsChanged();
			EmitChanged();
				
			// if user provided a callback, call it now
			if (userSaveAsyncCallback != null)
				userSaveAsyncCallback(ar);
			
			// notify that Save As has finished
			saveFinishedEvent.Set();
		}
	}
	
	///<summary>
	/// Save the buffer under the same filename, using an asynchronous model
	///</summary>
	public IAsyncResult BeginSave(ProgressCallback progressCallback, AsyncCallback ac) 
	{
		lock (LockObj) {		
			if (!fileOperationsAllowed) return null;
			
			saveFinishedEvent.Reset();
			userSaveAsyncCallback = ac;
			
			Thread saveThread = null;
			ThreadedAsyncResult tar = null;

			// decide whether to save in place or normally
			if (!fileBuf.IsResizable || this.Size == fileBuf.Size) {
				SaveInPlaceOperation sipo = new SaveInPlaceOperation(this, progressCallback, SaveInPlaceAsyncCallback, useGLibIdle);
				saveThread = new Thread(sipo.OperationThread);
				tar = new ThreadedAsyncResult(sipo, saveFinishedEvent, false);
			}
			else {
				SaveOperation so = new SaveOperation(this, TempFile.CreateName(tempDir), progressCallback, SaveAsyncCallback, useGLibIdle);
				saveThread = new Thread(so.OperationThread);
				tar = new ThreadedAsyncResult(so, saveFinishedEvent, false);
			}
			
			// don't allow messing up with the buffer
			// while we are saving
			// ...ReadAllowed is set in SaveOperation
			//this.ReadAllowed=false;
			this.ModifyAllowed = false;
			this.FileOperationsAllowed = false;
			
			this.EmitEvents = false;
			fsw.EnableRaisingEvents = false;
			
			// start save thread			
			saveThread.IsBackground = true;
			saveThread.Start();
			
			return tar;
		}
	}
	
	///<summary>
	/// Called when an asynchronous save operation finishes
	///</summary>
	void SaveAsyncCallback(IAsyncResult ar)
	{
		lock (LockObj) {
			SaveOperation so = (SaveOperation)ar.AsyncState;
			
			if (so.Result == SaveOperation.OperationResult.Finished) { // save went ok
				// No need to call CloseFile() MakePrivateCopyOfUndoRedo()
				// because it has already been called in SaveOperation

				LoadWithFile(so.SavePath);
				
				if (undoDeque.Count > 0)
					SaveCheckpoint = undoDeque.PeekFront();
				else
					SaveCheckpoint = null;
				
				changedBeyondUndo = false;
			}
			else if (so.Result == SaveOperation.OperationResult.Cancelled) { // save cancelled
				if (so.StageReached == SaveOperation.SaveStage.BeforeSaveAs) {
					System.IO.File.Delete(so.TempPath);
				}
				else if (so.StageReached == SaveOperation.SaveStage.BeforeDelete) {
					System.IO.File.Delete(so.TempPath);
					fileBuf.Load(so.SavePath);
				}
				else if (so.StageReached == SaveOperation.SaveStage.BeforeMove) {
					// cancel has no effect during move.
					// mark operation as successful
					so.Result = SaveOperation.OperationResult.Finished;
					LoadWithFile(so.SavePath);
				
					if (undoDeque.Count > 0)
						SaveCheckpoint = undoDeque.PeekFront();
					else
						SaveCheckpoint = null;
				}
			}
			else if (so.Result == SaveOperation.OperationResult.CaughtException) {
				if (so.StageReached == SaveOperation.SaveStage.BeforeSaveAs) {
					System.IO.File.Delete(so.TempPath);
				}
				else if (so.StageReached == SaveOperation.SaveStage.BeforeDelete) {
					System.IO.File.Delete(so.TempPath);
					fileBuf.Load(so.SavePath);
					// make sure FSW is valid (it is probably not
					// because bb.CloseFile has been called in SaveOperation)
					SetupFSW();
				}
				else if (so.StageReached == SaveOperation.SaveStage.BeforeMove) {
					// TO-DO: better handling?
					fileBuf.Load(so.SavePath);
				}
			}
			
			// re-allow buffer usage
			this.ReadAllowed = true;
			this.ModifyAllowed = true;
			this.FileOperationsAllowed = true;
			
			this.EmitEvents = true;
			fsw.EnableRaisingEvents = true;
			
			// notify the world about the changes			
			EmitPermissionsChanged();
			EmitChanged();			
			
			// if user provided a callback, call it now
			if (userSaveAsyncCallback != null)
				userSaveAsyncCallback(ar);
			
			// notify that Save has finished	
			saveFinishedEvent.Set();
		}
	}
	
	///<summary>
	/// Called when an asynchronous in-place save operation finishes
	///</summary>
	void SaveInPlaceAsyncCallback(IAsyncResult ar)
	{
		lock (LockObj) {
			SaveInPlaceOperation sipo = (SaveInPlaceOperation)ar.AsyncState;
			
			if (sipo.Result == ThreadedAsyncOperation.OperationResult.Finished) { // save went ok
				LoadWithFile(sipo.SavePath);
				
				if (undoDeque.Count > 0)
					SaveCheckpoint = undoDeque.PeekFront();
				else
					SaveCheckpoint = null;
				
				changedBeyondUndo = false;
			}
			else if (sipo.Result == ThreadedAsyncOperation.OperationResult.Cancelled) { // save cancelled
				
			}
			else if (sipo.Result == ThreadedAsyncOperation.OperationResult.CaughtException) {
				
			}
			
			// re-allow buffer usage
			this.ReadAllowed = true;
			this.ModifyAllowed = true;
			this.FileOperationsAllowed = true;
			
			this.EmitEvents = true;
			fsw.EnableRaisingEvents = true;
			
			// notify the world about the changes			
			EmitPermissionsChanged();
			EmitChanged();			
			
			// if user provided a callback, call it now
			if (userSaveAsyncCallback != null)
				userSaveAsyncCallback(ar);
			
			// notify that Save has finished	
			saveFinishedEvent.Set();
		}
	}
	
	///<summary> 
	/// Revert ByteBuffer to the last saved state
	///</summary> 
	public void Revert()
	{
		lock (LockObj) {
			if (!modifyAllowed) return;
			
			if (this.HasFile) {
				// reload file
				string filename = fileBuf.Filename;
				if (!File.Exists(filename))
					throw new FileNotFoundException(filename);
			
				fileBuf.Close();
				
				undoDeque.Clear();
				redoDeque.Clear();
				
				LoadWithFile(filename);

				
				SaveCheckpoint = null;
				changedBeyondUndo = false;
				
				// emit bytebuffer changed event
				EmitChanged();		
			}
		}
	}
	
	///<summary>
	/// Returns in a byte array the data contained in 
	/// the specified range in the buffer.  
	///</summary>
	public byte[] RangeToByteArray(IRange range)
	{
		if (range.Size==0)
			return null;
		
		byte[] rangeData=new byte[range.Size];
		
		long i=0;
		
		while (i < range.Size) {
			rangeData[i]=this[range.Start+i];
			i++;
		}
		
		return rangeData;
	}
	
	///<summary>
	/// Returns as a SegmentCollection the data contained in 
	/// the specified range in the buffer.  
	///</summary>
	public SegmentCollection RangeToSegmentCollection(Range range)
	{
		if (range.Size == 0)
			return null;
		
		return segCol.GetRange(range.Start, range.End);
	}
	
	///<summary> 
	/// Sets the file buffer and resets the segment collection
	///</summary> 
	private void LoadWithFile(string filename)
	{
		if (fileBuf == null)
			fileBuf = new FileBuffer(filename, 0xffff); // 64KB buffer
		else {
			fileBuf.Load(filename);
		}
		
		Segment s = new Segment(fileBuf, 0, fileBuf.Size-1);
		segCol = new SegmentCollection();
		segCol.Append(s);
		size = fileBuf.Size;
		
		SetupFSW();
		
		 
	}
	
	internal void MakePrivateCopyOfUndoRedo()
	{
		// Data in the actions may reference a file buffer that 
		// can become invalid (eg after saving a file)
		// Copy the data to private in-memory buffers to avoid data corruption
		// and crashes...
		//

		if (Preferences.Instance["Undo.KeepAfterSave"] == "Never") {
			undoDeque.Clear();
			redoDeque.Clear();
			return;
		}

		if (Preferences.Instance["Undo.KeepAfterSave"] == "Always") {
			foreach(ByteBufferAction action in undoDeque)
				action.MakePrivateCopyOfData();

			foreach(ByteBufferAction action in redoDeque)
				action.MakePrivateCopyOfData();

			return;
		}

		// if Preferences.Instance["Undo.KeepAfterSave"] == "Memory"
		// drop the undo and redo actions that don't fit into memory (and all actions
		// after them in the Deques).
		Deque<ByteBufferAction> newUndoDeque = new Deque<ByteBufferAction>();
		Deque<ByteBufferAction> newRedoDeque = new Deque<ByteBufferAction>();

		foreach(ByteBufferAction action in undoDeque) {
			long freeMem = long.MaxValue;
			try {	
				freeMem = Portable.GetAvailableMemory();
			}
			catch(NotImplementedException) {}

			if (freeMem < action.GetPrivateCopySize())
				break;

			action.MakePrivateCopyOfData();
			newUndoDeque.AddEnd(action);
		}
		
		
		foreach(ByteBufferAction action in redoDeque) {
			long freeMem = long.MaxValue;
			try {	
				freeMem = Portable.GetAvailableMemory();
			}
			catch(NotImplementedException) {}

			if (freeMem < action.GetPrivateCopySize())
				break;

			action.MakePrivateCopyOfData();
			newRedoDeque.AddEnd(action);
		}

		undoDeque.Clear();
		redoDeque.Clear();

		undoDeque = newUndoDeque;
		redoDeque = newRedoDeque;
	}
	
	private void SetupFSW()
	{	
		// monitor the file for changes
		if (fsw != null) {
			fsw.Dispose();
			fsw = null;
		}
			
		fsw = new FileSystemWatcher();
		fsw.Path = Path.GetDirectoryName(fileBuf.Filename);
		fsw.Filter = Path.GetFileName(fileBuf.Filename);
		fsw.NotifyFilter = NotifyFilters.FileName|NotifyFilters.LastAccess|NotifyFilters.LastWrite;
		fsw.Changed += new FileSystemEventHandler(OnFileChanged);
		//fsw.Deleted += new FileSystemEventHandler(OnFileChanged);
		
		fsw.EnableRaisingEvents = true;
	}
	
	private void OnFileChanged(object source, FileSystemEventArgs e)
	{
		EmitFileChanged();
	}
	
	
	
	public override byte this[long index] {
		set { } 
		get {
			lock (LockObj) {
				if (!readAllowed) { return 0;}
				
				long map; 
				Util.List<Segment>.Node node;
				Segment seg = segCol.FindSegment(index, out map, out node);
				//Console.WriteLine("Searching index {0} at {1}:{2}", index, map, seg);
				if (seg == null)
					throw new IndexOutOfRangeException(string.Format("ByteBuffer[{0}]",index));
				else {
					try {
						return seg.Buffer[seg.Start+index-map];	
					}
					catch(IndexOutOfRangeException) {
						Console.WriteLine("Problem at index {0} at {1}:{2}", index, map, seg);
						throw;
					}
				}
			}
		}
	}
	
	///<summary>
	/// Close the file associated with the ByteBuffer 
	///</summary>
	public void CloseFile()
	{
		lock (LockObj) {
			// close the file buffer and dispose the file watcher
			if (fileBuf != null && fileOperationsAllowed) {
				fileBuf.Close();
				fsw.Dispose();
				fsw = null;
				segCol = null;
				// buffer is in an unreadable state...
				this.ReadAllowed = false;
			}
		}
	}
	
	public override long Size {
		get { return size;}
	}

	public bool HasFile {
		get { return fileBuf != null; }
			
	}
	
	public string Filename {
		get {
			if (fileBuf != null)
				return fileBuf.Filename;
			else
				return this.autoFilename; 
			}	
	}

	public bool HasChanged {
		get {
			if (undoDeque.Count > 0)
				return (changedBeyondUndo || SaveCheckpoint != undoDeque.PeekFront());
			else
				return (changedBeyondUndo || SaveCheckpoint != null);
		}
	
	}
	
	public bool CanUndo {
		get { return (undoDeque.Count > 0); }
	}
	
	public bool CanRedo {
		get { return (redoDeque.Count > 0); }
	}
	
	public bool ActionChaining {
		get { return actionChaining; }
	}
	
	// Whether the ByteBuffer will emit events
	// (eg Changed event)
	public bool EmitEvents {
		get { return emitEvents; }
		set { emitEvents = value; }
	}
	
	// whether buffer can be safely read
	// by user eg to display data in a DataView.
	// if false reading from the buffer just returns zeroes
	public bool ReadAllowed { 
		get { return readAllowed; }
		set { 
			readAllowed = value;
			EmitPermissionsChanged();
		}
	}
	
	// Whether buffer can be modified.
	// If it is false, all buffer actions
	// that can modify the buffer are 
	// rendered ineffective.
	public bool ModifyAllowed {
		get { return modifyAllowed;}
		set { 
			modifyAllowed = value; 
			EmitPermissionsChanged();
		}
	}
	
	// Whether buffer can be saved, closed etc
	// If it is false, all save and close operations
	// are ignored.
	public bool FileOperationsAllowed {
		get { return fileOperationsAllowed;}
		set { 
			fileOperationsAllowed = value; 
			EmitPermissionsChanged();
		}
	}
	
	// Whether buffer can be resized (read-only property)
	public bool IsResizable {
		get {
			if (fileBuf != null)
				return fileBuf.IsResizable;
			else
				return true;
		}
	}
	// Use the GLib Idle handler for progress reporting.
	// Mandatory if progress reporting involves Gtk+ widgets.
	public bool UseGLibIdle {
		get { return useGLibIdle; }
		set { useGLibIdle = value; }
	}
	
	// The maximum number of actions the Buffer
	// will be able to undo
	public int MaxUndoActions {
		get { return maxUndoActions; }
		set { 
			maxUndoActions = value;
			if (maxUndoActions != -1) {
				// if we are going to remove undo actions,
				// mark that we won't be able to get back to 
				// the original buffer state
				if (undoDeque.Count > maxUndoActions)
					changedBeyondUndo = true;
				
				// clear all undo actions beyond the limit
				while (undoDeque.Count > maxUndoActions) {
					undoDeque.RemoveEnd();
				}
				
			}
		}
	}
	
	///<summary>
	/// The directory where temporary files are stored 
	///</summary>
	public string TempDir {
		get { return tempDir; }
		set { tempDir = value;}
	}
	
	///<summary>
	/// The range of the buffer as a Util.Range 
	///</summary>
	public Util.Range Range {
		get { 
			Util.Range range = new Util.Range();
			if (size > 0) {
				range.Start = 0;
				range.End = size - 1;
			}
			
			return range;
		}
	}
	
	internal void Display(string s)
	{
		Console.Write(s);
		segCol.List.Display();
	}

}

} // end namespace 
