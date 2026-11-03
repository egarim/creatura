using System.Globalization;
using System.Text;
using Creatura.Scenarios;

namespace Creatura.Operator;

/// <summary>
/// Follows a replay file as a run writes it: each <see cref="Poll"/> reads what was added since the last one and hands
/// every complete line to the model; a line still being written waits for its end. The file is opened for reading
/// only, shared, and closed again after each poll, so the run writing it is never in the way and nothing is written.
/// </summary>
public sealed class ReplayFollower(string path, RunModel model)
{
    private long _position;
    private readonly List<byte> _partial = [];

    public string Path { get; } = path;

    public RunModel Model { get; private set; } = model;

    /// <summary>True when the file got shorter than what was read (a new run over the same name): the model was started again.</summary>
    public bool Restarted { get; private set; }

    /// <summary>Reads what is new; returns how many complete lines were applied. A missing file is not an error: nothing yet.</summary>
    public int Poll()
    {
        Restarted = false;
        if (!File.Exists(Path)) return 0;
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position)
        {
            _position = 0;
            _partial.Clear();
            Model = new RunModel();
            Restarted = true;
        }

        stream.Seek(_position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var applied = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            _position += read;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    _partial.Add(buffer[i]);
                    continue;
                }

                var line = Encoding.UTF8.GetString([.. _partial]).TrimEnd('\r');
                _partial.Clear();
                Model.Apply(line);
                applied++;
            }
        }

        return applied;
    }
}

/// <summary>
/// A run in this process, watched live: the scenario calls it as an <see cref="IScenarioObserver"/>, and it writes each
/// line exactly as <see cref="JsonlReplayWriter"/> writes a replay file and hands it to the model, so a run watched live
/// and its file read back show the same rows. <paramref name="dispatch"/> moves each line onto the screen's thread.
/// </summary>
public sealed class LiveRun : IScenarioObserver
{
    private readonly JsonlReplayWriter _writer;

    public LiveRun(RunModel model, Action<Action>? dispatch = null)
    {
        Model = model;
        _writer = new JsonlReplayWriter(new LineSink(line => (dispatch ?? (a => a()))(() => model.Apply(line))));
    }

    public RunModel Model { get; }

    public void Start(ReplayHeader header) => _writer.Start(header);

    public void Tick(ReplayTick tick) => _writer.Tick(tick);

    public void End(ReplaySummary summary) => _writer.End(summary);

    /// <summary>A text writer that hands on each line it is given, and keeps nothing.</summary>
    private sealed class LineSink(Action<string> line) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                line(_pending.ToString().TrimEnd('\r'));
                _pending.Clear();
            }
            else
            {
                _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "") Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }
}
