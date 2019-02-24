using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    /// <summary>
    /// Simple collection of a small set of key/value pairs, with the "environment" key explicitly stored.
    /// </summary>
    public sealed class LabelDict
    {
        private readonly string _environment;
        private readonly IList<KeyValuePair<string, string>> _labels = new List<KeyValuePair<string, string>>();

        public LabelDict([NotNull] LabelDict other)
        {
            _environment = other._environment;
            _labels = new List<KeyValuePair<string, string>>(other._labels);
        }

        public LabelDict([NotNull] string environment)
        {
            _environment = environment;
        }

        public void Set(string key, string value)
        {
            for (var i = 0; i < _labels.Count; ++i)
                if (_labels[i].Key == key)
                {
                    _labels[i] = new KeyValuePair<string, string>(key, value);
                    return;
                }

            _labels.Add(new KeyValuePair<string, string>(key, value));
        }

        [CanBeNull]
        public string Get(string key)
        {
            return _labels.Where(_ => _.Key == key).Select(_ => _.Value).SingleOrDefault();
        }

        private bool Equals(LabelDict other)
        {
            if (_environment != other._environment)
                return false;

            if (_labels.Count != other._labels.Count)
                return false;

            for (var i = 0; i < _labels.Count; ++i)
                if (_labels[i].Key != other._labels[i].Key || _labels[i].Value != other._labels[i].Value)
                    return false;

            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is LabelDict other && Equals(other);
        }

        public override int GetHashCode()
        {
            var result = _environment.GetHashCode();
            foreach (var (key, value) in _labels)
                result = result * 31 + key.GetHashCode() * 17 + value.GetHashCode();

            return result;
        }

        /// <summary>
        /// Convert this instance to a string suited to be enclosed in curly braces for prometheus metric exposure.
        /// </summary>
        /// <param name="le">Optional "le" key for histogram buckets.</param>
        /// <returns></returns>
        public string ToString(string le)
        {
            var result = $"environment={Quote(_environment)}";
            if (!string.IsNullOrEmpty(le))
                result += ",le=" + Quote(le);

            foreach (var (key, value) in _labels)
                result += $",{key}={Quote(value)}";

            return result;
        }

        /// <summary>
        /// Sanitize a string and enclose it in quotes to be used as a label value.
        /// </summary>
        /// <param name="s">The string to be be quoted.</param>
        /// <returns>The quoted and escaped string.</returns>
        private static string Quote([NotNull] string s)
        {
            return $"\"{s.Replace(@"\", @"\\").Replace("\n", @"\n").Replace("\"", "\\\"")}\"";
        }

        public int Count => _labels.Count;
    }
}
