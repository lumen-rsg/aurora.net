using System.Xml;
using System.IO;

namespace Aurora.Core.Parsing;

public static class RepoMdParser
{
    public class RepoDataRef
    {
        public string Type { get; set; } = "";
        public string Location { get; set; } = "";
        public string Checksum { get; set; } = "";
    }

    public static RepoDataRef? GetPrimaryDbInfo(string xmlContent)
    {
        using var reader = XmlReader.Create(new StringReader(xmlContent));
        
        while (reader.ReadToFollowing("data"))
        {
            var type = reader.GetAttribute("type");
            // "primary_db" is the pre-generated SQLite database
            if (type == "primary_db") 
            {
                var repoRef = new RepoDataRef { Type = type };
                
                using var subtree = reader.ReadSubtree();
                while (subtree.Read())
                {
                    if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "location")
                    {
                        repoRef.Location = subtree.GetAttribute("href") ?? "";
                    }
                    else if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "checksum")
                    {
                        repoRef.Checksum = subtree.ReadElementContentAsString();
                    }
                }
                return repoRef;
            }
        }
        return null;
    }
}