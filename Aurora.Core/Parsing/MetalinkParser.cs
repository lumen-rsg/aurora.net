using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace Aurora.Core.Parsing;

public static class MetalinkParser
{
    public static List<string> ParseMirrorUrls(string metalinkXml)
    {
        var urls = new List<(string Url, int Preference)>();

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new StringReader(metalinkXml), settings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "url")
                continue;

            var protocol = reader.GetAttribute("protocol") ?? "";
            var prefStr = reader.GetAttribute("preference") ?? "0";
            var url = reader.ReadElementContentAsString().Trim();

            if (string.IsNullOrEmpty(url)) continue;

            int pref = int.TryParse(prefStr, out var p) ? p : 0;

            // Boost HTTPS preference
            if (protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                pref += 10000;

            urls.Add((url, pref));
        }

        return urls
            .OrderByDescending(u => u.Preference)
            .Select(u => u.Url)
            .ToList();
    }
}
