namespace RAM.Core.Roblox;

/// <summary>
/// Abstraction over user-facing prompts so RAM.Core stays UI-agnostic.
/// The WinUI shell implements this with ContentDialogs.
/// </summary>
public interface IAccountNotifier
{
    void Info(string message, string? title = null);
    void Warn(string message, string? title = null);
    void Error(string message, string? title = null);
}
