namespace Ember.ViewModels;

/// <summary>One overlay preset as listed on the Overlay page (shared by the Windows and Linux apps).</summary>
public sealed class PresetRow : Observable
{
    public string Id { get; init; } = "";

    private string _name = "", _summary = "", _hotkeyText = "", _note = "", _command = "";
    private bool _isActive, _isRecording, _hasHotkey;

    /// <summary>Edited in place; saved when the box loses focus or Enter is pressed.</summary>
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }
    /// <summary>A message under the hotkey, e.g. that the keys are already used elsewhere.</summary>
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;
    /// <summary>Linux without global keys: the command to bind to a shortcut yourself.</summary>
    public string Command { get => _command; set => Set(ref _command, value); }
    /// <summary>The overlay is showing exactly this preset right now.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsRecording
    {
        get => _isRecording;
        set { if (Set(ref _isRecording, value)) Raise(nameof(RecordText)); }
    }
    public bool HasHotkey
    {
        get => _hasHotkey;
        set { if (Set(ref _hasHotkey, value)) Raise(nameof(RecordText)); }
    }
    public string RecordText => _isRecording ? "Cancel" : _hasHotkey ? "Change" : "Set hotkey";
}
