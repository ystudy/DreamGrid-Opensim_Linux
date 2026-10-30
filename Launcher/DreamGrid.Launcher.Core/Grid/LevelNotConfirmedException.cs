namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// A <c>set user level</c> reached Robust's console, but the account didn't show the new level in time (or Robust stopped
/// answering while it was being checked): the change may have been made or not. Told apart from a console that couldn't be
/// reached (<see cref="HttpRequestException"/>), where nothing was sent. The message carries the console transcript.
/// </summary>
public sealed class LevelNotConfirmedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
