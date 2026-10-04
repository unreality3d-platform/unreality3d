using System.Text.RegularExpressions;

namespace U3D.Editor
{
    // Mirrors toLookupForm in functions/src/utils/validation.js, which writes the
    // creator_projects record the router matches. Change both together.
    public static class U3DUrlUtility
    {
        public const string BaseUrl = "https://unreality3d.com/";

        public static string ToLookupForm(string raw)
        {
            if (raw == null)
            {
                return "";
            }

            var s = raw.Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[\s_]+", "-");
            s = Regex.Replace(s, @"[^a-z0-9-]", "");
            s = Regex.Replace(s, @"-+", "-");
            return s.Trim('-');
        }

        public static string CreatorUrl(string creatorUsername)
        {
            return $"{BaseUrl}{ToLookupForm(creatorUsername)}/";
        }

        public static string ProfessionalUrl(string creatorUsername, string repositoryName)
        {
            return $"{BaseUrl}{ToLookupForm(creatorUsername)}/{ToLookupForm(repositoryName)}/";
        }
    }
}