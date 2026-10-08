#if DEBUG
using System;
using System.Collections.Generic;

namespace TrayTrigger.Services;

/// <summary>
/// The reads only the <c>--test-dlss</c> report needs, so none of this is in a release build.
/// </summary>
public static unsafe partial class NvApi
{
    private const uint IdDrsEnumSettings            = 0xAE3039DA;

    /// <summary>Where a value came from, in the words the report prints.</summary>
    public static string OriginLabel(DrsSettingValue value) => value.Origin switch
    {
        SettingOrigin.ApplicationProfile => value.IsCurrentPredefined ? "Predefined" : "UserSet",
        SettingOrigin.GlobalProfile => "Inherited (Global profile)",
        SettingOrigin.BaseProfile => "Inherited (base profile)",
        _ => "DriverDefault"
    };

    public sealed partial class Session
    {
        /// <summary>Every DWORD setting explicitly stored on a profile.</summary>
        public List<DrsSettingValue> EnumSettings(IntPtr profile, out string? error)
        {
            error = null;
            var result = new List<DrsSettingValue>();
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint*, NvDrsSetting*, int>)Lookup(IdDrsEnumSettings);
            if (fn == null) { error = "NvAPI_DRS_EnumSettings not exposed."; return result; }

            // The driver ends enumeration with a non-zero status; the bound is only a guard against
            // a driver that never does.
            for (uint index = 0; index < 4096; index++)
            {
                var s = new NvDrsSetting { Version = VersionOf<NvDrsSetting>(1) };
                uint count = 1;
                int status = fn(_handle, profile, index, &count, &s);
                if (status != 0 || count == 0) break;
                if (s.SettingType != 0) continue;

                result.Add(ToValue(&s));
            }
            return result;
        }
    }
}
#endif
