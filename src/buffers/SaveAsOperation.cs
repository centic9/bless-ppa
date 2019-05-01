// created on 3/28/2005 at 3:19 PM
/*
 *   Copyright (c) 2005, Alexandros Frantzis (alf82 [at] freemail [dot] gr)
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

using System;
using System.IO;
using System.Threading;
using Bless.Util;
using Mono.Unix;

namespace Bless.Buffers {

///<summary>
/// Saves the contents of a ByteBuffer using an asynchronous threaded model
///</summary>
public class SaveAsOperation : ThreadedAsyncOperation, ISaveState
{
	protected ByteBuffer byteBuffer;
	protected long bytesSaved;
	
	protected string savePath;
	FileStream fs;
	
	SaveAsStage stageReached;
	
	public ByteBuffer Buffer {
		get {return byteBuffer;}
	}
	
	public string SavePath {
		get { return savePath; }
		set { savePath=value; }
	}
	
	public long BytesSaved {
		get {return bytesSaved;}
	}
	
	public enum SaveAsStage { BeforeCreate, BeforeWrite }
	
	public SaveAsStage StageReached {
		get { return stageReached; }
	}
	
	public SaveAsOperation(ByteBuffer bb, string fn, ProgressCallback pc,
							AsyncCallback ac, bool glibIdle): base(pc, ac, glibIdle)
	{
		byteBuffer=bb;
		savePath=fn;
		fs=null;
		bytesSaved=0;
	}
	
	protected bool CheckFreeSpace(string path, long extraSpace)
	{
		try {
			long freeSpace = Portable.GetAvailableDiskSpace(path);
			//System.Console.WriteLine("CFS {0}: {1}+{2} {3}", path, freeSpace, extraSpace, byteBuffer.Size);

			return (freeSpace + extraSpace >= byteBuffer.Size);
		}
		catch (NotImplementedException) {
			return true;	
		}

	}

	protected override bool StartProgress()
	{
		progressCallback(string.Format(Catalog.GetString("Saving '{0}'"), SavePath), ProgressAction.Message);
		return progressCallback(((double)bytesSaved)/byteBuffer.Size, ProgressAction.Show);
	}
	
	protected override bool UpdateProgress()
	{
		return progressCallback(((double)bytesSaved)/byteBuffer.Size, ProgressAction.Update);
	}
	
	protected override bool EndProgress()
	{
		return progressCallback(((double)bytesSaved)/byteBuffer.Size, ProgressAction.Destroy);
	}
	
	protected override void DoOperation()
	{
		stageReached = SaveAsStage.BeforeCreate;

		if (!CheckFreeSpace(Path.GetDirectoryName(savePath), 0)) {
			string msg = string.Format(Catalog.GetString("There is not enough free space on the device to save file '{0}'."), savePath);
			throw new IOException(msg);
		}

		// try to open in append mode first, so that a sharing violation
		// doesn't end up with the file truncated (as opposed
		// to using FileMode.Create)
		fs = new FileStream(savePath, FileMode.Append, FileAccess.Write);
		fs.Close();
		
		// do the actual create
		fs = new FileStream(savePath, FileMode.Create, FileAccess.Write);
		
		stageReached = SaveAsStage.BeforeWrite;
		
		const int blockSize = 0xffff;
		
		byte[] baTemp = new byte[blockSize];
		
		// for every node
		Util.List<Segment>.Node node = byteBuffer.segCol.List.First;
		
		while (node != null && !cancelled)
		{
			// Save the data in the node 
			// in blocks of blockSize each
			Segment s = node.data;
			long len = s.Size;
			long nBlocks = len/blockSize;
			int last = (int)(len % blockSize); // bytes in last block
			long i;
		
			// for every full block
			for (i = 0; i < nBlocks; i++) {
				s.Buffer.Read(baTemp, 0, s.Start + i * blockSize, blockSize);
				fs.Write(baTemp, 0, blockSize);
				bytesSaved = (i + 1) * blockSize;
				
				if (cancelled)
					break;	
			}
		
			// if last non-full block is not empty
			if (last != 0 && !cancelled) {
				s.Buffer.Read(baTemp, 0, s.Start + i * blockSize, last);
				fs.Write(baTemp, 0, last);
			}
					
			node = node.next;
		}	
		
		fs.Close();
		fs = null;	
	}
	
	protected override void EndOperation()
	{
		if (fs != null) {
			fs.Close();
			fs = null;
		}
	}
}

} // end namespace
