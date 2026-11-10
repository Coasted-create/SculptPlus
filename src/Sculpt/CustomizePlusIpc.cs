using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SculptPlus;

/// <summary> The few things Customize+ lets other plugins do: read a profile, and put a temporary one on a character. </summary>
public sealed class CustomizePlusIpc
{
    private readonly ICallGateSubscriber<(int, int)> _version;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> _activeProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> _profileById;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> _setTemporary;
    private readonly ICallGateSubscriber<ushort, int> _deleteTemporary;
    private readonly ICallGateSubscriber<Guid, (int, List<(Guid, string, List<(string, System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3, bool, bool, bool, System.Numerics.Vector3, bool)>, bool)>?)> _templates;
    private readonly ICallGateSubscriber<Guid, Guid, int> _enableTemplate;
    private readonly ICallGateSubscriber<Guid, Guid, int> _disableTemplate;

    public CustomizePlusIpc(IDalamudPluginInterface pi)
    {
        _version         = pi.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        _activeProfile   = pi.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        _profileById     = pi.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        _setTemporary    = pi.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        _deleteTemporary = pi.GetIpcSubscriber<ushort, int>("CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");
        _templates       = pi.GetIpcSubscriber<Guid, (int, List<(Guid, string, List<(string, System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3, bool, bool, bool, System.Numerics.Vector3, bool)>, bool)>?)>("CustomizePlus.Profile.GetTemplates");
        _enableTemplate  = pi.GetIpcSubscriber<Guid, Guid, int>("CustomizePlus.Profile.EnableTemplateByUniqueId");
        _disableTemplate = pi.GetIpcSubscriber<Guid, Guid, int>("CustomizePlus.Profile.DisableTemplateByUniqueId");
    }

    /// <summary> True when Customize+ is loaded and answers. </summary>
    public bool Available
    {
        get
        {
            try
            {
                return _version.InvokeFunc().Item1 >= 6;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary> The id of the profile active on a character (a saved one or a temporary one), null when there is none. </summary>
    public Guid? ActiveProfileId(ushort objectIndex)
    {
        try
        {
            var (code, id) = _activeProfile.InvokeFunc(objectIndex);
            return code == 0 ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary> The JSON ({"Bones":{…}}) of a saved profile (temporary ones are not given out), null when there is none. </summary>
    public string? ProfileJson(Guid id)
    {
        try
        {
            var (code, json) = _profileById.InvokeFunc(id);
            return code == 0 ? json : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary> The templates of a saved profile, in order, with whether each one is switched on. </summary>
    public List<(Guid Id, string Name, int Bones, bool On)>? Templates(Guid profile)
    {
        try
        {
            var (code, list) = _templates.InvokeFunc(profile);
            return code != 0 || list is null ? null : list.Select(t => (t.Item1, t.Item2, t.Item3.Count, t.Item4)).ToList();
        }
        catch
        {
            return null;
        }
    }

    public bool SetTemplate(Guid profile, Guid template, bool on)
    {
        try
        {
            return (on ? _enableTemplate : _disableTemplate).InvokeFunc(profile, template) == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool SetTemporary(ushort objectIndex, string profileJson)
    {
        try
        {
            return _setTemporary.InvokeFunc(objectIndex, profileJson).Item1 == 0;
        }
        catch
        {
            return false;
        }
    }

    public void DeleteTemporary(ushort objectIndex)
    {
        try
        {
            _deleteTemporary.InvokeFunc(objectIndex);
        }
        catch
        {
            // not there
        }
    }
}
