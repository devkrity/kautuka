namespace Kautuka.Control.Core.Tray;

/// <summary>No-op tray manager for platforms where tray is not supported.</summary>
public sealed class NullTrayManager : ITrayManager
{
    public void Show(string tooltip, TrayMenuItem[] menu, Action? onActivate = null) { }
    public void Hide() { }
    public void UpdateStatus(bool gatewayOnline) { }
    public void ShowBalloon(string title, string message) { }
    public void Dispose() { }
}
