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
        /// <summary>
        /// NVIDIA's values that a user value is currently sitting over. The driver keeps them: a
        /// user write hides a predefined value, it does not destroy it, and only
        /// RestoreSettingDefault brings it back.
        /// </summary>
        public Dictionary<uint, uint> HiddenPredefined { get; } = new();
        /// <summary>Applications attached to the profile. One, unless a test adds more.</summary>
        public int Applications { get; set; } = 1;
    }

    /// <summary>When set, every profile lookup fails with this - an error, not "not found".</summary>
    public string? FindError { get; set; }

    /// <summary>Setting ids whose reads fail - an error, not "absent".</summary>
    public HashSet<uint> UnreadableSettingIds { get; } = new();

    /// <summary>What each lookup was asked for, so "by full path" is observable.</summary>
    public List<string> FindRequests { get; } = new();

    public FakeProfile AddProfile(string exeName, string profileName, bool isPredefined = true)
    {
        var profile = new FakeProfile { Name = profileName, IsPredefined = isPredefined };
        Profiles[exeName] = profile;
        return profile;
    }

    /// <summary>Setting ids this driver pretends not to have.</summary>
    public HashSet<uint> UnknownSettingIds { get; } = new();

    public string? GetSettingName(uint settingId) =>
        UnknownSettingIds.Contains(settingId) ? null : $"Setting 0x{settingId:X8}";

    public IDrsSession? OpenSession(out string? error)
    {
        error = OpenError;
        if (OpenError != null) return null;
        SessionsOpened++;
        return new FakeSession(this);
    }

    private sealed class FakeSession : IDrsSession
    {
        // A session is a private copy of the database, exactly as NVAPI behaves: loaded on open,
        // changed in memory, and written back only by Save. So a refused Save leaves the database
        // untouched, and reads inside the session see its own unsaved changes.
        private readonly FakeDrsBackend _owner;
        private readonly Dictionary<string, Working> _profiles = new(StringComparer.OrdinalIgnoreCase);

        private sealed class Working
        {
            public required string Name;
            public bool IsPredefined;
            public int Applications = 1;
            public Dictionary<uint, (uint Value, bool IsPredefined)> Settings = new();
            public Dictionary<uint, uint> HiddenPredefined = new();
        }

        public FakeSession(FakeDrsBackend owner)
        {
            _owner = owner;
            foreach (var (exe, p) in owner.Profiles)
            {
                _profiles[exe] = new Working
                {
                    Name = p.Name,
                    IsPredefined = p.IsPredefined,
                    Applications = p.Applications,
                    Settings = new(p.Settings),
                    HiddenPredefined = new(p.HiddenPredefined)
                };
            }
        }

        public DrsProfileHandle? FindProfileForExecutable(string exePathOrFileName, out string? error)
        {
            _owner.FindRequests.Add(exePathOrFileName);
            error = _owner.FindError;
            if (error != null) return null;
            // The driver matches on the file name whether or not it was handed a path.
            return _profiles.TryGetValue(System.IO.Path.GetFileName(exePathOrFileName), out var p)
                ? new DrsProfileHandle(IntPtr.Zero, p.Name, p.IsPredefined)
                : null;   // not found is an answer, not an error
        }

        public DrsProfileHandle? CreateProfileForExecutable(string profileName, string exeFileName, out string? error)
        {
            error = null;
            _owner.CreatedProfiles.Add(profileName);
            _profiles[exeFileName] = new Working { Name = profileName };
            return new DrsProfileHandle(IntPtr.Zero, profileName, false);
        }

        public DrsSettingReading? GetSetting(DrsProfileHandle profile, uint settingId, out string? error)
        {
            error = null;
            if (_owner.UnreadableSettingIds.Contains(settingId)) { error = "NVAPI_ERROR (-1)"; return null; }

            var p = Resolve(profile);
            if (p != null && p.Settings.TryGetValue(settingId, out var held))
                return new DrsSettingReading(held.Value, held.IsPredefined ? DlssSettingOrigin.Predefined : DlssSettingOrigin.UserSet);

            if (_owner.GlobalProfile.TryGetValue(settingId, out uint global))
                return new DrsSettingReading(global, DlssSettingOrigin.Inherited);

            return null;   // absent at every layer
        }

        public bool SetSetting(DrsProfileHandle profile, uint settingId, uint value, out string? error)
        {
            error = null;
            var p = Resolve(profile);
            if (p == null) { error = "no such profile"; return false; }
            // A user write hides NVIDIA's value rather than destroying it.
            if (p.Settings.TryGetValue(settingId, out var held) && held.IsPredefined)
                p.HiddenPredefined[settingId] = held.Value;
            // A written value is never predefined - that is what makes it distinguishable later.
            p.Settings[settingId] = (value, false);
            return true;
        }

        public bool DeleteSetting(DrsProfileHandle profile, uint settingId, out string? error)
        {
            error = null;
            var p = Resolve(profile);
            if (p == null) { error = "no such profile"; return false; }
            // Deliberately strict: deleting is for a value that was never NVIDIA's. Whatever the
            // real driver does here, the code under test must not depend on it.
            if (p.HiddenPredefined.ContainsKey(settingId) || (p.Settings.TryGetValue(settingId, out var held) && held.IsPredefined))
            {
                error = "predefined setting: restore it, do not delete it";
                return false;
            }
            p.Settings.Remove(settingId);
            return true;
        }

        public bool RestoreSettingDefault(DrsProfileHandle profile, uint settingId, out string? error)
        {
            error = null;
            var p = Resolve(profile);
            if (p == null) { error = "no such profile"; return false; }
            if (p.HiddenPredefined.Remove(settingId, out uint predefined))
                p.Settings[settingId] = (predefined, true);
            else if (!(p.Settings.TryGetValue(settingId, out var held) && held.IsPredefined))
                p.Settings.Remove(settingId);
            return true;
        }

        public (uint Applications, uint Settings)? GetProfileCounts(DrsProfileHandle profile)
        {
            var p = Resolve(profile);
            return p == null ? null : ((uint)p.Applications, (uint)p.Settings.Count);
        }

        public bool DeleteProfile(DrsProfileHandle profile, out string? error)
        {
            error = null;
            string? key = _profiles.FirstOrDefault(kv => kv.Value.Name == profile.Name).Key;
            if (key == null) { error = "no such profile"; return false; }
            _profiles.Remove(key);
            return true;
        }

        public bool Save(out string? error)
        {
            error = _owner.SaveError;
            if (error != null) return false;

            foreach (string gone in _owner.Profiles.Keys.Where(k => !_profiles.ContainsKey(k)).ToList())
                _owner.Profiles.Remove(gone);

            foreach (var (exe, w) in _profiles)
            {
                // Written back into the same object, so a test holding a profile still sees it.
                if (!_owner.Profiles.TryGetValue(exe, out var target) || target.Name != w.Name)
                    _owner.Profiles[exe] = target = new FakeProfile { Name = w.Name, IsPredefined = w.IsPredefined };

                target.Applications = w.Applications;
                target.Settings.Clear();
                foreach (var (id, v) in w.Settings) target.Settings[id] = v;
                target.HiddenPredefined.Clear();
                foreach (var (id, v) in w.HiddenPredefined) target.HiddenPredefined[id] = v;
            }

            _owner.SaveCount++;
            _owner.AfterSave?.Invoke();
            return true;
        }

        private Working? Resolve(DrsProfileHandle handle) =>
            _profiles.Values.FirstOrDefault(p => p.Name == handle.Name);

        public void Dispose() { }
    }
}
