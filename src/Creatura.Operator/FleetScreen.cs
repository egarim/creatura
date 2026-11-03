using System.Globalization;
using Creatura.Cells;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Creatura.Operator;

/// <summary>How a Fleet row is drawn: blocked or stressed in red, quiet dimmed, otherwise plain.</summary>
public enum FleetLook
{
    Plain,
    Alarm,
    Dim,
}

/// <summary>
/// The Fleet view on a terminal (docs/design-console.md, U3): who is working on what, now, one row per cell, the most
/// recently seen first, read again every few seconds. Blocked and stressed cells are red, quiet ones dimmed, revoked
/// ones absent (the model leaves them out). Keys: Enter opens the cell's activity in the Run view, o offers it a goal
/// (typed on the bottom line, Enter to send, Esc to cancel), r reads again, q or Esc leaves.
/// </summary>
public sealed class FleetScreen : Window
{
    /// <summary>The stress at which a cell is drawn red even when it did not say it was blocked.</summary>
    public const double AlarmStress = 0.7;

    private readonly IApplication _app;
    private readonly FleetModel _model;
    private readonly Label _title = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private readonly TableView _table = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly Label _status = new() { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label _prompt = new() { X = 0, Y = Pos.AnchorEnd(1), Height = 1, Visible = false };
    private readonly TextField _goal = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Visible = false };
    private readonly Label _keys = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private FleetSnapshot _snapshot = new(DateTimeOffset.UtcNow, []);
    private FleetRow? _offeringTo;

    private static readonly Scheme Red = new(new Attribute(ColorName16.BrightRed, ColorName16.Black));
    private static readonly Scheme Gray = new(new Attribute(ColorName16.DarkGray, ColorName16.Black));

    public FleetScreen(IApplication app, FleetModel model)
    {
        _app = app;
        _model = model;
        Title = "creatura console: fleet";
        _table.Style.ShowHorizontalHeaderOverline = false;
        _table.Style.ExpandLastColumn = true;
        _table.Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < _snapshot.Rows.Count
            ? LookOf(_snapshot.Rows[args.RowIndex]) switch { FleetLook.Alarm => Red, FleetLook.Dim => Gray, _ => null }
            : null;
        Add(_title, _table, _status, _prompt, _goal, _keys);
        _table.KeyDown += OnKey;
        KeyDown += OnKey;
        _goal.KeyDown += OnGoalKey;
        Reload();
        _app.AddTimeout(TimeSpan.FromSeconds(5), () =>
        {
            if (_offeringTo is null) Reload();
            return true;
        });
    }

    /// <summary>How a row is drawn: red when blocked or at <see cref="AlarmStress"/> or above, dimmed when quiet.</summary>
    public static FleetLook LookOf(FleetRow row) =>
        row.Blocked || row.Stress >= AlarmStress ? FleetLook.Alarm : row.Quiet ? FleetLook.Dim : FleetLook.Plain;

    private void Reload()
    {
        var selected = Selected()?.Cell;
        _snapshot = _model.Read();
        var now = _snapshot.At;
        _title.Text = _snapshot.Problem is { } problem
            ? "the Fleet could not be read: " + problem
            : string.Create(CultureInfo.InvariantCulture, $"{_snapshot.Rows.Count} cell{(_snapshot.Rows.Count == 1 ? "" : "s")}, ")
              + string.Create(CultureInfo.InvariantCulture, $"{_snapshot.Rows.Count(r => r.Claim is not null)} at work, {_snapshot.Rows.Count(r => r.Quiet)} quiet, {_snapshot.Rows.Count(r => LookOf(r) == FleetLook.Alarm)} in trouble")
              + $"; read {now.ToLocalTime():HH:mm:ss}";
        _keys.Text = "Enter its activity   o offer a goal   r read again   q quit";
        _table.Table = new EnumerableTableSource<FleetRow>(_snapshot.Rows, new Dictionary<string, Func<FleetRow, object>>
        {
            ["cell"] = r => r.Name,
            ["creatura"] = r => r.Creatura,
            ["seen"] = r => (r.Quiet ? "quiet, " : "") + FleetView.Ago(now - r.LastSeen) + " ago",
            ["work"] = r => r.Claim is null ? "" : $"{r.Claim} ({FleetView.Ago(now - r.ClaimedAt!.Value)})",
            ["stress"] = r => r.Stress is { } s ? s.ToString("0.00", CultureInfo.InvariantCulture) + (r.Blocked ? " BLOCKED" : "") : r.Blocked ? "BLOCKED" : "",
            ["offers"] = r => r.PendingOffers > 0 ? r.PendingOffers.ToString(CultureInfo.InvariantCulture) : "",
            ["last progress"] = r => r.Progress ?? "",
        });
        var index = selected is null ? -1 : _snapshot.Rows.ToList().FindIndex(r => r.Cell == selected);
        if (index >= 0) _table.Value = new TableSelection(new System.Drawing.Point(0, index));
        _table.Update();
        SetNeedsDraw();
    }

    private FleetRow? Selected() =>
        _table.Value is { } v && v.SelectedCell.Y >= 0 && v.SelectedCell.Y < _snapshot.Rows.Count ? _snapshot.Rows[v.SelectedCell.Y] : _snapshot.Rows.FirstOrDefault();

    private void OnKey(object? sender, Key key)
    {
        if (_offeringTo is not null) return;
        if (key == Key.Q || key == Key.Esc)
        {
            _app.RequestStop();
            key.Handled = true;
        }
        else if (key == Key.R)
        {
            Reload();
            key.Handled = true;
        }
        else if (key == Key.Enter && Selected() is { } row)
        {
            key.Handled = true;
            var run = _model.CellRun(row);
            using var screen = new RunScreen(_app, run, null, null) { Title = "creatura console: " + row.Name };
            _app.Run(screen);
            Reload();
        }
        else if (key == Key.O && Selected() is { } target)
        {
            key.Handled = true;
            _offeringTo = target;
            _prompt.Text = $"goal for {target.Name}: ";
            _prompt.Width = _prompt.Text.Length;
            _goal.X = _prompt.Text.Length;
            _goal.Text = "";
            _keys.Visible = false;
            _prompt.Visible = true;
            _goal.Visible = true;
            _goal.SetFocus();
            SetNeedsDraw();
        }
    }

    private void OnGoalKey(object? sender, Key key)
    {
        if (_offeringTo is not { } target) return;
        if (key == Key.Esc)
        {
            CloseOffer("no offer made");
            key.Handled = true;
        }
        else if (key == Key.Enter)
        {
            var (ok, said) = _model.Offer(target.Cell, _goal.Text ?? "");
            CloseOffer(ok ? said.Split('\n')[0] : "not offered: " + said);
            key.Handled = true;
            Reload();
        }
    }

    private void CloseOffer(string status)
    {
        _offeringTo = null;
        _status.Text = status;
        _prompt.Visible = false;
        _goal.Visible = false;
        _keys.Visible = true;
        _table.SetFocus();
        SetNeedsDraw();
    }
}
