Option Explicit

Dim shell, fileSystem, appFolder, timelineDll, command, argument
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

appFolder = fileSystem.GetParentFolderName(WScript.ScriptFullName)
timelineDll = fileSystem.BuildPath(appFolder, "AppSafe\Timeline.dll")

If Not fileSystem.FileExists(timelineDll) Then
    MsgBox "Timeline has not been built yet. Run Build Timeline.cmd first.", vbExclamation, "Timeline"
    WScript.Quit 1
End If

command = "dotnet """ & timelineDll & """"
For Each argument In WScript.Arguments
    command = command & " " & argument
Next
shell.Run command, 0, False
