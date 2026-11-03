using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Creatura.Operator;

/// <summary>
/// The Run view on a terminal (docs/design-console.md, slice U1): one run, tick by tick, as a transcript. It draws the
/// model and nothing else; following a file, it polls for new lines and keeps the newest row in view while the person
/// is at the bottom. Keys: c the next cell (every cell, then each one), f stop or resume following, q or Esc to leave.
/// </summary>
public sealed class RunScreen : Window
{
    private readonly IApplication _app;
    private readonly ReplayFollower? _follower;
    private readonly Label _title = new() { X = 0, Y = 0, Width = Dim.Fill() };
    private readonly Label _counts = new() { X = 0, Y = 1, Width = Dim.Fill() };
    private readonly TableView _table = new() { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1), FullRowSelect = true };
    private readonly Label _keys = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private RunModel _model;
    private string? _cell;
    private bool _following;
    private List<RunRow> _rows = [];

    private static readonly Scheme Red = new(new Attribute(ColorName16.BrightRed, ColorName16.Black));
    private static readonly Scheme Yellow = new(new Attribute(ColorName16.Yellow, ColorName16.Black));

    public RunScreen(IApplication app, RunModel model, ReplayFollower? follower, string? cell)
    {
        _app = app;
        _model = model;
        _follower = follower;
        _cell = cell;
        _following = follower is not null;
        Title = "creatura console: run";
        _table.Style.ShowHorizontalHeaderOverline = false;
        _table.Style.ExpandLastColumn = true;
        _table.Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < _rows.Count ? _rows[args.RowIndex].Mark switch
        {
            RowMark.Blocked => Yellow,
            RowMark.Refused or RowMark.Flagged => Red,
            _ => null,
        } : null;
        Add(_title, _counts, _table, _keys);
        // The table is focused and would take letters for its type-to-find, so the keys are read before it does.
        _table.KeyDown += OnKey;
        KeyDown += OnKey;
        Refresh(toEnd: _following);
        if (_follower is not null) _app.AddTimeout(TimeSpan.FromMilliseconds(300), Poll);
    }

    private bool Poll()
    {
        if (!_following || _follower is null) return true;
        var atEnd = AtEnd();
        if (_follower.Poll() > 0 || _follower.Restarted)
        {
            _model = _follower.Model;
            Refresh(toEnd: atEnd);
        }

        return true;
    }

    private bool AtEnd() => _rows.Count == 0 || _table.Value is not { } v || v.SelectedCell.Y >= _rows.Count - 1;

    private void Refresh(bool toEnd)
    {
        _rows = [.. _model.RowsOf(_cell)];
        _title.Text = RunText.Title(_model) + (_model.Header is { Goal.Length: > 0 } h ? "  goal: " + h.Goal : "");
        _counts.Text = (_cell is null ? "every cell" : "cell " + _cell) + ": " + RunText.Counts(_model, _cell)
            + (_model.Ended && _model.Outcome.Count > 0 ? "  |  " + _model.Outcome[0] : "");
        _keys.Text = "c next cell   f " + (_follower is null ? "(not following)" : _following ? "stop following" : "follow") + "   q quit"
            + (_follower is null ? "" : "   " + _follower.Path);
        foreach (var label in new[] { _title, _counts, _keys })
        {
            // Setting Text sizes a label to its text; the three lines keep the window's width and one row.
            label.Width = Dim.Fill();
            label.Height = 1;
        }

        _table.Table = new EnumerableTableSource<RunRow>(_rows, new Dictionary<string, Func<RunRow, object>>
        {
            ["tick"] = r => r.Tick,
            ["cell"] = r => r.Cell,
            ["mark"] = r => RunText.MarkText(r.Mark),
            ["act"] = r => r.Act,
            ["verdict"] = r => r.Verdict,
            ["saw"] = r => r.Observation,
            ["answer"] = r => r.Answer,
        });
        if (toEnd && _rows.Count > 0) _table.MoveCursorToEndOfTable(false, null!);
        _table.Update();
        SetNeedsDraw();
    }

    private void OnKey(object? sender, Key key)
    {
        if (key == Key.Q || key == Key.Esc)
        {
            _app.RequestStop();
            key.Handled = true;
        }
        else if (key == Key.C)
        {
            var cells = _model.Cells;
            var i = _cell is null ? -1 : cells.ToList().IndexOf(_cell);
            _cell = i + 1 < cells.Count ? cells[i + 1] : null;
            Refresh(toEnd: false);
            key.Handled = true;
        }
        else if (key == Key.F && _follower is not null)
        {
            _following = !_following;
            if (_following) Poll();
            Refresh(toEnd: _following);
            key.Handled = true;
        }
    }
}
