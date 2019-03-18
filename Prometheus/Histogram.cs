using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class Histogram : LabeledMetric
    {
        public static readonly double[] DefaultBuckets =
            {.005, .01, .025, .05, .075, .1, .25, .5, .75, 1.0, 2.5, 5.0, 7.5, 10.0, double.PositiveInfinity};

        private readonly string _bucketName;

        private readonly double[] _buckets;
        private readonly string _countName;
        private readonly ULongScalar[] _counts;
        private readonly Scalar _sum = new Scalar();
        private readonly string _sumName;

        /// <summary>
        /// Placeholder value for the "le" label, to be replaced with the bucket border when exposed.
        /// </summary>
        private const string LePlaceholder = "$$$$$";

        public Histogram([NotNull] MetricBase metricBase, [NotNull] LabelDict labels, [NotNull] double[] buckets)
            : base(metricBase, labels)
        {
            Debug.Assert(metricBase.Type == MetricsType.Histogram);
            _buckets = buckets.OrderBy(_ => _).ToArray();
            if (_buckets.Length == 0)
                _buckets = DefaultBuckets;
            else if (!double.IsPositiveInfinity(_buckets.Last()))
                _buckets = _buckets.AsEnumerable().Concat(new[] {double.PositiveInfinity}).ToArray();

            if (_buckets.Length < 2)
                throw new ArgumentException("Must at least provide one bucket", nameof(buckets));

            _counts = Enumerable.Range(0, _buckets.Length).Select(_ => new ULongScalar()).ToArray();
            _bucketName = ExtendBaseName(QualifiedName(LePlaceholder), "_bucket");
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        public override void ExposeTo(StreamWriter stream)
        {
            ulong count = 0;
            for (var i = 0; i < _buckets.Length; ++i)
            {
                count = _counts[i].Get();
                stream.WriteLine("{0} {1}", _bucketName.Replace(LePlaceholder, ToGoString(_buckets[i])), count);
            }

            stream.WriteLine("{0} {1}", _countName, count);
            stream.WriteLine("{0} {1}", _sumName, _sum);
        }

        public override void Add(double value)
        {
            _sum.Add(value);
            for (var i = 0; i < _buckets.Length; ++i)
                if (value <= _buckets[i])
                {
                    _counts[i].Add(1);
                }
        }
    }
}