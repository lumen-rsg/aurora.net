using System.Xml;
using System.IO;
using System.Collections.Generic;

namespace Aurora.Core.Parsing;

public static class RepoMdParser
{
    public class RepoDataRef
    {
        public string Type { get; set; } = "";
        public string Location { get; set; } = "";
        public string ChecksumType { get; set; } = "";
        public string ChecksumValue { get; set; } = "";
        public string OpenChecksumType { get; set; } = "";
        public string OpenChecksumValue { get; set; } = "";
        public long Timestamp { get; set; }
        public long Size { get; set; }
        public long OpenSize { get; set; }

        // Backward compat
        public string Checksum => ChecksumValue;
    }

    private static readonly XmlReaderSettings _xmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreWhitespace = true
    };

    public static Dictionary<string, RepoDataRef> GetAllDataRefs(string xmlContent)
    {
        var result = new Dictionary<string, RepoDataRef>();

        using var reader = XmlReader.Create(new StringReader(xmlContent), _xmlSettings);

        while (reader.ReadToFollowing("data"))
        {
            var type = reader.GetAttribute("type");
            if (string.IsNullOrEmpty(type)) continue;

            var dataRef = ParseDataElement(reader);
            dataRef.Type = type;
            result[type] = dataRef;
        }

        return result;
    }

    public static RepoDataRef? GetDataRef(string xmlContent, string dataType)
    {
        using var reader = XmlReader.Create(new StringReader(xmlContent), _xmlSettings);

        while (reader.ReadToFollowing("data"))
        {
            var type = reader.GetAttribute("type");
            if (type == dataType)
            {
                var dataRef = ParseDataElement(reader);
                dataRef.Type = type!;
                return dataRef;
            }
        }

        return null;
    }

    public static RepoDataRef? GetPrimaryDbInfo(string xmlContent)
    {
        return GetDataRef(xmlContent, "primary_db") ?? GetDataRef(xmlContent, "primary");
    }

    public static RepoDataRef? GetPrimaryXmlInfo(string xmlContent)
    {
        return GetDataRef(xmlContent, "primary");
    }

    public static RepoDataRef? GetGroupInfo(string xmlContent)
    {
        return GetDataRef(xmlContent, "group_gz") ?? GetDataRef(xmlContent, "group");
    }

    public static RepoDataRef? GetFilelistsDbInfo(string xmlContent)
    {
        return GetDataRef(xmlContent, "filelists_db") ?? GetDataRef(xmlContent, "filelists");
    }

    public static RepoDataRef? GetFilelistsXmlInfo(string xmlContent)
    {
        return GetDataRef(xmlContent, "filelists");
    }

    private static RepoDataRef ParseDataElement(XmlReader reader)
    {
        var dataRef = new RepoDataRef();

        using var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element) continue;

            switch (subtree.LocalName)
            {
                case "location":
                    dataRef.Location = subtree.GetAttribute("href") ?? "";
                    break;

                case "checksum":
                    dataRef.ChecksumType = subtree.GetAttribute("type") ?? "sha256";
                    dataRef.ChecksumValue = subtree.ReadElementContentAsString().Trim();
                    break;

                case "open-checksum":
                    dataRef.OpenChecksumType = subtree.GetAttribute("type") ?? "sha256";
                    dataRef.OpenChecksumValue = subtree.ReadElementContentAsString().Trim();
                    break;

                case "timestamp":
                    if (long.TryParse(subtree.ReadElementContentAsString().Trim(), out var ts))
                        dataRef.Timestamp = ts;
                    break;

                case "size":
                    if (long.TryParse(subtree.ReadElementContentAsString().Trim(), out var sz))
                        dataRef.Size = sz;
                    break;

                case "open-size":
                    if (long.TryParse(subtree.ReadElementContentAsString().Trim(), out var osz))
                        dataRef.OpenSize = osz;
                    break;
            }
        }

        return dataRef;
    }
}
