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
        private readonly ULongScalar _observations = new ULongScalar();
        private readonly Scalar _sum = new Scalar();
        private readonly string _sumName;

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
            _bucketName = QualifiedName("$$$$$");
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        public override void ExposeTo(StreamWriter stream)
        {
            for (var i = 0; i < _buckets.Length; ++i)
                stream.WriteLine("{0} {1}", _bucketName.Replace("$$$$$", ToGoString(_buckets[i])), _counts[i]);

            stream.WriteLine("{0} {1}", _countName, _observations);
            stream.WriteLine("{0} {1}", _sumName, _sum);
        }

        public override void Add(double value)
        {
            _observations.Add(1);
            _sum.Add(value);
            for (var i = 0; i < _buckets.Length; ++i)
                if (value <= _buckets[i])
                {
                    _counts[i].Add(1);
                    break;
                }
        }
    }
}