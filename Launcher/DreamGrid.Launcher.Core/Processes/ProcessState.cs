namespace DreamGrid.Launcher.Core.Processes;

/// <summary>Lifecycle of a supervised child process.</summary>
public enum ProcessState { Stopped, Starting, Running, Stopping, Crashed, Failed }
