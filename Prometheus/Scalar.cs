using System.Globalization;
using System.Threading;

namespace csv_prometheus_exporter.Prometheus;

/// <summary>
///   Thread safe double scalar with a limited set of operations.
/// </summary>
public sealed class Scalar
{
  private SpinLock _lock;
  private double _value;

  internal Scalar(double value = 0.0)
  {
    _value = value;
  }

  internal void Add(double d)
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
      {
        _lock.Exit();
      }
    }
  }

  internal void Set(double d)
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
      {
        _lock.Exit();
      }
    }
  }

  internal double Get()
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
      {
        _lock.Exit();
      }
    }
  }

  private bool Equals(Scalar other)
  {
    return _value.Equals(other._value);
  }

  public override bool Equals(object? obj)
  {
    if (ReferenceEquals(null, obj))
    {
      return false;
    }

    return obj is Scalar other && Equals(other);
  }

  public override int GetHashCode()
  {
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    return _value.GetHashCode();
  }

  public static bool operator ==(Scalar left, Scalar right)
  {
    return Equals(left, right);
  }

  public static bool operator !=(Scalar left, Scalar right)
  {
    return !Equals(left, right);
  }

  public override string ToString()
  {
    return Get().ToString(CultureInfo.InvariantCulture);
  }
}

/// <summary>
///   Thread safe ulong scalar with a limited set of operations.
/// </summary>
public sealed class ULongScalar
{
  private SpinLock _lock;
  private ulong _value;

  internal ULongScalar(ulong value = 0)
  {
    _value = value;
  }

  internal void Add(ulong d)
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
      {
        _lock.Exit();
      }
    }
  }

  private ulong Get()
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
      {
        _lock.Exit();
      }
    }
  }

  private bool Equals(ULongScalar other)
  {
    return _value.Equals(other._value);
  }

  public override bool Equals(object? obj)
  {
    if (ReferenceEquals(null, obj))
    {
      return false;
    }

    return obj is ULongScalar other && Equals(other);
  }

  public override int GetHashCode()
  {
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    return _value.GetHashCode();
  }

  public static bool operator ==(ULongScalar left, ULongScalar right)
  {
    return Equals(left, right);
  }

  public static bool operator !=(ULongScalar left, ULongScalar right)
  {
    return !Equals(left, right);
  }

  public override string ToString()
  {
    return Get().ToString();
  }
}
