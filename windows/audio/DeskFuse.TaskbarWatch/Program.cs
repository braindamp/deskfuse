using DeskFuse.Audio;

// Deliberately expose only the read-only taskbar watch, never arbitrary speaker
// commands (which include device creation and an audio stimulus).
if (args.Length > 1 || (args.Length == 1 && !int.TryParse(args[0], out _)))
{
    Console.Error.WriteLine("Usage: DeskFuse.TaskbarWatch [seconds: 5..120]");
    return 1;
}

var seconds = args.Length == 0 ? "30" : args[0];
return await SpeakerCommands.RunAsync(["taskbar-watch", "--seconds", seconds]);
