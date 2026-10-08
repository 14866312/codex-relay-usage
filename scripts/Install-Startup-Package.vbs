Option Explicit
Dim shell, fs, root, exe
Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
root = fs.GetParentFolderName(WScript.ScriptFullName)
exe = fs.BuildPath(root, "CodexRelayUsage.exe")
shell.CurrentDirectory = root
shell.Run Chr(34) & exe & Chr(34) & " --install-startup", 0, False
