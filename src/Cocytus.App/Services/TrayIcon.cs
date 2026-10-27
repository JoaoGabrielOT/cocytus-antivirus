using System.Drawing;
using System.Windows.Forms;

namespace Cocytus.App.Services;

/// <summary>Ícone na área de notificação, com menu e avisos (balões/notificações do Windows).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private Action? _onBalloonClick;

    public TrayIcon(Action open, Action showRealTime, Action exit)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Abrir o Cocytus", null, (_, _) => open());
        _menu.Items.Add("Tempo real", null, (_, _) => showRealTime());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Sair", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Text = "Cocytus",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => _onBalloonClick?.Invoke();
    }

    public void Notify(string title, string text, bool warning, Action? onClick)
    {
        _onBalloonClick = onClick;
        _icon.ShowBalloonTip(10_000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    private static Icon LoadIcon()
    {
        var exe = Environment.ProcessPath;
        return (exe is null ? null : Icon.ExtractAssociatedIcon(exe)) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
