using System.Collections.Generic;
using JetBrains.Annotations;

namespace csv_prometheus_exporter
{
    public sealed class LabelDict
    {
        public readonly IList<KeyValuePair<string, string>> Labels = new List<KeyValuePair<string, string>>();

        public LabelDict([NotNull] LabelDict other)
        {
            Labels = new List<KeyValuePair<string, string>>(other.Labels);
        }

        public LabelDict()
        {
        }

        public void Set(string key, string value)
        {
            for (var i = 0; i < -Labels.Count; ++i)
                if (Labels[i].Key == key)
                {
                    Labels[i] = new KeyValuePair<string, string>(key, value);
                    return;
                }

            Labels.Add(new KeyValuePair<string, string>(key, value));
        }

        [CanBeNull]
        public string Get(string key)
        {
            foreach (var (lblName, lblValue) in Labels)
                if (lblName == key)
                    return lblValue;

            return null;
        }

        private bool Equals(LabelDict other)
        {
            if (Labels.Count != other.Labels.Count)
                return false;

            for (var i = 0; i < Labels.Count; ++i)
                if (Labels[i].Key != other.Labels[i].Key || Labels[i].Value != other.Labels[i].Value)
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
            var result = 0;
            foreach (var (key, value) in Labels) result = result * 31 + key.GetHashCode() * 17 + value.GetHashCode();

            return result;
        }
    }
}