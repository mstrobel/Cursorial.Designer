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

        // Where the framework comes from is decided ONCE, up front, from metadata alone — the
        // ready payload reports it so the IDE can narrate degraded states without parsing logs.
        // The user's build-output directory arrives as a spawn-time argument (--user-dir <path>):
        // the plugin spawns one host per preview tab and restarts it when the output changes, so
        // the directory never needs to change mid-process.
        var resolution = FrameworkResolution.Resolve(AppContext.BaseDirectory, ParseUserDirectory(args));

        Emit(new ReadyEvent
        {
            ProtocolVersion = PreviewProtocol.Version,
            Pid = Environment.ProcessId,
            FrameworkVersion = resolution.FrameworkVersion,
            FrameworkSource = resolution.FrameworkSource,
            FrameworkPath = resolution.FrameworkDirectory,
            FallbackReason = resolution.FallbackReason,
            UserDirMissing = resolution.UserDirMissing ? true : null, // omitted from the wire when false
        });

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

        // The core (and with it the entire framework) loads HERE, on the loop thread, into the
        // load context CoreLoader builds — so the thread that executes commands is the one the
        // headless host binds to, exactly as before the launcher/core split.
        using var session = CoreLoader.CreateSession(Emit, resolution);

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

    /// <summary>The value following <c>--user-dir</c>, or null. Unknown arguments are ignored.</summary>
    private static string? ParseUserDirectory(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--user-dir")
                return args[i + 1];
        }

        return null;
    }
}
