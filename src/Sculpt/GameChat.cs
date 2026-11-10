using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace SculptPlus;

/// <summary> Runs one of the game's own text commands (an emote), as if you typed it. Framework thread. </summary>
public static unsafe class GameChat
{
    public static bool Run(string command)
    {
        if (command.Length is 0 or > 80 || command[0] != '/')
            return false;
        var ui = UIModule.Instance();
        if (ui == null)
            return false;
        var text = Utf8String.FromString(command);
        try
        {
            ui->ProcessChatBoxEntry(text);
            return true;
        }
        finally
        {
            text->Dtor(true);
        }
    }
}
