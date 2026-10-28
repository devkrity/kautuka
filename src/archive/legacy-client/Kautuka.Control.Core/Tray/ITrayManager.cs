namespace Kautuka.Control.Core.Tray;

public sealed record TrayMenuItem
{
    public string? Label { get; init; }
    public Action? OnClick { get; init; }
    public bool IsSeparator { get; init; }

    public static TrayMenuItem Item(string label, Action onClick) =>
        new() { Label = label, OnClick = onClick };

    public static TrayMenuItem Separator() =>
        new() { IsSeparator = true };
}

public interface ITrayManager : IDisposable
{
    /// <summary>Shows the tray icon with the given menu. onActivate is called when the user activates the icon (double-click on Windows).</summary>
    void Show(string tooltip, TrayMenuItem[] menu, Action? onActivate = null);
    void Hide();
    /// <summary>Updates the icon colour to reflect Gateway health (green = online, grey = offline).</summary>
    void UpdateStatus(bool gatewayOnline);
    /// <summary>Shows a brief balloon/toast notification from the tray icon. No-op on platforms that do not support it.</summary>
    void ShowBalloon(string title, string message);
}
