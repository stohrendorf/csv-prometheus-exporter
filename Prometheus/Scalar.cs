using System.Globalization;
using System.Threading;

namespace csv_prometheus_exporter.Prometheus
{
    public struct Scalar
    {
        private double _value;
        private SpinLock _lock;

        public Scalar(double value = 0.0)
        {
            _value = value;
            _lock = new SpinLock();
        }

        public void Add(double d)
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                _value += d;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }

        public void Set(double d)
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                _value = d;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }

        public double Get()
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                return _value;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }

        public bool Equals(Scalar other)
        {
            return _value.Equals(other._value);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is Scalar other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public static bool operator ==(Scalar left, Scalar right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Scalar left, Scalar right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Get().ToString(CultureInfo.InvariantCulture);
        }
    }

    public struct ULongScalar
    {
        private ulong _value;
        private SpinLock _lock;

        public ULongScalar(ulong value = 0)
        {
            _value = value;
            _lock = new SpinLock();
        }

        public void Add(ulong d)
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                _value += d;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }

        public void Set(ulong d)
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                _value = d;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }

        public ulong Get()
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                return _value;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }

        public bool Equals(ULongScalar other)
        {
            return _value.Equals(other._value);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is ULongScalar other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public static bool operator ==(ULongScalar left, ULongScalar right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ULongScalar left, ULongScalar right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Get().ToString();
        }
    }
}