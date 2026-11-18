namespace OakProyektor.App;

internal enum WindowKind { Control, Output }

[Flags]
internal enum Targets { None = 0, Control = 1, Output = 2, All = Control | Output }
