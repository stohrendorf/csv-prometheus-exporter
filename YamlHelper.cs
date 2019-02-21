using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace csv_prometheus_exporter
{
    public static class YamlHelper
    {
        public static string String(this YamlMappingNode node, string key)
        {
            if (node.Children.TryGetValue(new YamlScalarNode(key), out var n))
                return ((YamlScalarNode) n).Value;
            return null;
        }

        public static int? Int(this YamlMappingNode node, string key)
        {
            if (node.Children.TryGetValue(new YamlScalarNode(key), out var n)
                && int.TryParse(((YamlScalarNode) n).Value, out var result))
                return result;
            return null;
        }

        public static YamlMappingNode Map(this YamlMappingNode node, string key)
        {
            if (node.Children.TryGetValue(new YamlScalarNode(key), out var n))
                return (YamlMappingNode) n;
            return null;
        }

        public static YamlSequenceNode List(this YamlMappingNode node, string key)
        {
            if (node.Children.TryGetValue(new YamlScalarNode(key), out var n))
                return (YamlSequenceNode) n;
            return null;
        }

        public static IEnumerable<string> StringList(this YamlMappingNode node, string key)
        {
            foreach (var element in node.List(key)) yield return ((YamlScalarNode) element).Value;
        }

        public static IEnumerable<KeyValuePair<string, YamlNode>> StringMap(this YamlMappingNode node,
            string key)
        {
            foreach (var (yamlKey, value) in node.Map(key)?.Children ??
                                             Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>())
                yield return new KeyValuePair<string, YamlNode>(((YamlScalarNode) yamlKey).Value,
                    value is YamlScalarNode scalar && scalar.Value == "~" ? null : value);
        }
    }
}