<buildinfo>
	<type>library</type>
	<input>*.cs</input>
	<ignore>MainWindow.cs</ignore>
	<input>dialogs/*.cs</input>
	<package>gtk-sharp-2.0</package>
	<package>glade-sharp-2.0</package>
	<reference>Mono.Posix</reference>
	<output>Bless.Gui.dll</output>
	<extra>-nowarn:0169</extra>
</buildinfo>