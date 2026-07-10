using System.Text;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// The process shell: owns stdin/stdout, keeps the wire single-writer, and runs the command
/// loop on the calling thread — which thereby becomes the preview session's UI thread (the
/// headless host is thread-affine to its creator). A dedicated reader thread pumps stdin so a
/// blocked read never stalls frame emission.
/// </summary>
internal static class StdioServer
{
    public static int Run(string[] args)
    {
        // The wire owns the real stdout. User assemblies loaded into this process may write to
        // Console.Out (logging, stray Debug prints) — redirect that to stderr so a single rogue
        // Console.WriteLine cannot corrupt the event stream.
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.SetOut(Console.Error);

        var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var outputLock = new object();

        void Emit(PreviewEvent @event)
        {
            var line = PreviewProtocol.Serialize(@event);
            lock (outputLock)
            {
                output.WriteLine(line);
                output.Flush();
            }
        }

        Emit(new ReadyEvent { ProtocolVersion = PreviewProtocol.Version, Pid = Environment.ProcessId });

        var commands = new System.Collections.Concurrent.BlockingCollection<PreviewCommand>(boundedCapacity: 256);

        var reader = new Thread(() =>
        {
            try
            {
                while (input.ReadLine() is { } line)
                {
                    if (line.Length == 0)
                        continue;

                    PreviewCommand command;
                    try
                    {
                        command = PreviewProtocol.DeserializeCommand(line);
                    }
                    catch (Exception ex)
                    {
                        Emit(new ErrorEvent { Message = "Malformed command line.", Detail = ex.Message });
                        continue;
                    }

                    commands.Add(command);
                    if (command is ShutdownCommand)
                        break;
                }
            }
            catch (Exception ex)
            {
                Emit(new ErrorEvent { Message = "stdin reader failed.", Detail = ex.ToString() });
            }
            finally
            {
                commands.CompleteAdding(); // EOF or shutdown: the session loop drains and exits
            }
        })
        {
            IsBackground = true,
            Name = "preview-stdin",
        };
        reader.Start();

        using var session = new PreviewSession(Emit);

        foreach (var command in commands.GetConsumingEnumerable())
        {
            if (command is ShutdownCommand)
            {
                Emit(new LogEvent { Level = "info", Message = "Shutting down." });
                break;
            }

            try
            {
                session.Execute(command);
            }
            catch (Exception ex)
            {
                Emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Command '{command.GetType().Name}' failed.", Detail = ex.ToString() });
            }
        }

        // The reader exits promptly on both paths (it breaks right after enqueuing shutdown, and
        // EOF completes on its own); joining before disposal avoids racing CompleteAdding against
        // Dispose. If it is somehow still stuck in ReadLine, skip disposal — the process is
        // exiting and the background thread dies with it.
        if (reader.Join(TimeSpan.FromSeconds(2)))
            commands.Dispose();

        return 0;
    }
}
