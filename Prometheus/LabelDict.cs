using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using NUnit.Framework;

namespace csv_prometheus_exporter.Prometheus
{
    /// <summary>
    /// Simple collection of a small set of key/value pairs, with the "environment" key explicitly stored.
    /// </summary>
    public sealed class LabelDict
    {
        internal readonly string Environment;
        private readonly IList<KeyValuePair<string, string>> _labels = new List<KeyValuePair<string, string>>();

        public LabelDict([NotNull] LabelDict other)
        {
            Environment = other.Environment;
            _labels = new List<KeyValuePair<string, string>>(other._labels);
        }

        public LabelDict([NotNull] string environment)
        {
            if (string.IsNullOrEmpty(environment))
                throw new ArgumentNullException(nameof(environment), "Environment must contain a value");
            Environment = environment;
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
            if (Environment != other.Environment)
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
            var result = Environment.GetHashCode();
            foreach (var (key, value) in _labels)
                result = result * 31 + key.GetHashCode() * 17 + value.GetHashCode();

            return result;
        }

        /// <summary>
        /// Convert this instance to a string suited to be enclosed in curly braces for prometheus metric exposure.
        /// </summary>
        /// <param name="le">Optional "le" key for histogram buckets.</param>
        /// <returns></returns>
        public string ToString([CanBeNull] string le)
        {
            var result = $"environment={Quote(Environment)}";
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

    [TestFixture]
    public class LabelDictTest
    {
        [Test]
        public void TestConstruction()
        {
            Assert.That(() => new LabelDict((string) null), Throws.TypeOf<ArgumentNullException>());
            Assert.That(() => new LabelDict(""), Throws.TypeOf<ArgumentNullException>());

            var d = new LabelDict("foo");
            Assert.That(d.Environment, Is.EqualTo("foo"));
            Assert.That(d.Count, Is.EqualTo(0));

            var d2 = new LabelDict(d);
            Assert.That(d2.Environment, Is.EqualTo("foo"));
            Assert.That(d2.Count, Is.EqualTo(0));
        }

        [Test]
        public void TestEquality()
        {
            var d1 = new LabelDict("foo");
            var d2 = new LabelDict(d1);
            var d3 = new LabelDict("foo");
            var d4 = new LabelDict("bar");
            var d5 = new LabelDict("foo");
            d5.Set("bar", "baz");
            var d6 = new LabelDict("foo");
            d6.Set("bar", "baz2");

            Assert.That(d1, Is.EqualTo(d1));
            Assert.That(d1, Is.Not.EqualTo(null));
            Assert.That(d1, Is.EqualTo(d2));
            Assert.That(d1, Is.EqualTo(d3));
            Assert.That(d1, Is.EqualTo(d2));
            Assert.That(d1, Is.Not.EqualTo(d4));
            Assert.That(d1, Is.Not.EqualTo(d5));
            Assert.That(d5, Is.Not.EqualTo(d6));
        }

        [Test]
        public void TestSet()
        {
            var d = new LabelDict("foo");

            d.Set("baz", "baz");
            Assert.That(d.Count, Is.EqualTo(1));
            Assert.That(d.Get("baz"), Is.EqualTo("baz"));

            d.Set("baz", "baz2");
            Assert.That(d.Count, Is.EqualTo(1));
            Assert.That(d.Get("baz"), Is.EqualTo("baz2"));

            d.Set("foo", "bar");
            Assert.That(d.Count, Is.EqualTo(2));
            Assert.That(d.Get("baz"), Is.EqualTo("baz2"));
            Assert.That(d.Get("foo"), Is.EqualTo("bar"));
        }

        [Test]
        public void TestToString()
        {
            var d = new LabelDict("foo");
            Assert.That(d.ToString(null), Is.EqualTo("environment=\"foo\""));
            Assert.That(d.ToString(""), Is.EqualTo("environment=\"foo\""));
            Assert.That(d.ToString("123"), Is.EqualTo("environment=\"foo\",le=\"123\""));

            d = new LabelDict("foo\nbar");
            Assert.That(d.ToString(null), Is.EqualTo("environment=\"foo\\nbar\""));

            d = new LabelDict("foo\"bar");
            Assert.That(d.ToString(null), Is.EqualTo("environment=\"foo\\\"bar\""));

            d = new LabelDict("foo\\bar");
            Assert.That(d.ToString(null), Is.EqualTo("environment=\"foo\\\\bar\""));

            d = new LabelDict("foo");
            d.Set("a", "b");
            Assert.That(d.ToString(null), Is.EqualTo("environment=\"foo\",a=\"b\""));
        }
    }
}