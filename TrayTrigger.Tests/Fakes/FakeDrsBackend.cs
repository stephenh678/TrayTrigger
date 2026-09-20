using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests.Fakes;

/// <summary>
/// An in-memory stand-in for the NVIDIA driver settings database, so the ownership rules can be
/// exercised without an NVIDIA machine.
///
/// <para>It models the one structural fact the rules depend on: a setting has a value <i>and</i> a
/// layer it came from. A fake that stored only values would let every test pass while the real
/// code confused a user's own setting with one inherited from the Global profile - which is the
/// exact mistake the ownership record exists to prevent.</para>
/// </summary>
public sealed class FakeDrsBackend : IDrsBackend
{
    /// <summary>Profiles by executable name, as the driver matches them.</summary>
    public Dictionary<string, FakeProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Values every profile inherits when it has nothing of its own.</summary>
    public Dictionary<uint, uint> GlobalProfile { get; } = new();

    public bool IsAvailable { get; set; } = true;

    /// <summary>When set, opening a session fails with this message.</summary>
    public string? OpenError { get; set; }

    /// <summary>When set, Save fails with this message - the unelevated-write case.</summary>
    public string? SaveError { get; set; }

    /// <summary>How many times Save actually reached "disk". Nothing should persist without it.</summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// Sessions opened. The write-back check must use a second one: NVIDIA's own documentation
    /// says DRS sessions do not merge, so reading through the session that wrote confirms nothing.
    /// </summary>
    public int SessionsOpened { get; private set; }

    /// <summary>
    /// Runs immediately after a successful Save, to stage what the write-back check will find -
    /// the "the driver said yes but the value is not there" case.
    /// </summary>
    public Action? AfterSave { get; set; }

    /// <summary>Profiles created during the test, so "NVIDIA did not know this game" is observable.</summary>
    public List<string> CreatedProfiles { get; } = new();

    public sealed class FakeProfile
    {
        public required string Name { get; init; }
        public bool IsPredefined { get; init; }
        /// <summary>Settings held on this profile, with whether the value is NVIDIA's own.</summary>
        public Dictionary<uint, (uint Value, bool IsPredefined)> Settings { get; } = new();
    }

    public FakeProfile AddProfile(string exeName, string profileName, bool isPredefined = true)
    {
        var profile = new FakeProfile { Name = profileName, IsPredefined = isPredefined };
        Profiles[exeName] = profile;
        return profile;
    }

    public IDrsSession? OpenSession(out string? error)
    {
        error = OpenError;
        if (OpenError != null) return null;
        SessionsOpened++;
        return new FakeSession(this);
    }

    private sealed class FakeSession(FakeDrsBackend owner) : IDrsSession
    {
        // Writes are staged until Save, exactly as NVAPI behaves, so a test can prove that a
        // refused Save leaves the database untouched.
        private readonly List<Action> _pending = new();

        public DrsProfileHandle? FindProfileForExecutable(string exeFileName, out string? error)
        {
            error = null;
            if (owner.Profiles.TryGetValue(exeFileName, out var p))
                return new DrsProfileHandle(IntPtr.Zero, p.Name, p.IsPredefined);
            error = "NVAPI_EXECUTABLE_NOT_FOUND (-166)";
            return null;
        }

        public DrsProfileHandle? CreateProfileForExecutable(string profileName, string exeFileName, out string? error)
        {
            error = null;
            owner.CreatedProfiles.Add(profileName);
            var p = owner.AddProfile(exeFileName, profileName, isPredefined: false);
            return new DrsProfileHandle(IntPtr.Zero, p.Name, false);
        }

        public DrsSettingReading? GetSetting(DrsProfileHandle profile, uint settingId, out string? error)
        {
            error = null;
            var p = Resolve(profile);
            if (p != null && p.Settings.TryGetValue(settingId, out var held))
                return new DrsSettingReading(held.Value, held.IsPredefined ? DlssSettingOrigin.Predefined : DlssSettingOrigin.UserSet);

            if (owner.GlobalProfile.TryGetValue(settingId, out uint global))
                return new DrsSettingReading(global, DlssSettingOrigin.Inherited);

            error = "NVAPI_SETTING_NOT_FOUND (-160)";
            return null;
        }

        public bool SetSetting(DrsProfileHandle profile, uint settingId, uint value, out string? error)
        {
            error = null;
            var p = Resolve(profile);
            if (p == null) { error = "no such profile"; return false; }
            // A written value is never predefined - that is what makes it distinguishable later.
            _pending.Add(() => p.Settings[settingId] = (value, false));
            return true;
        }

        public bool DeleteSetting(DrsProfileHandle profile, uint settingId, out string? error)
        {
            error = null;
            var p = Resolve(profile);
            if (p == null) { error = "no such profile"; return false; }
            _pending.Add(() => p.Settings.Remove(settingId));
            return true;
        }

        public bool Save(out string? error)
        {
            error = owner.SaveError;
            if (error != null) return false;
            foreach (var write in _pending) write();
            _pending.Clear();
            owner.SaveCount++;
            owner.AfterSave?.Invoke();
            return true;
        }

        private FakeProfile? Resolve(DrsProfileHandle handle) =>
            owner.Profiles.Values.FirstOrDefault(p => p.Name == handle.Name);

        public void Dispose() { }
    }
}
