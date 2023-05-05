using System.Collections.Generic;
using YamlDotNet.Serialization;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace csv_prometheus_exporter.Scraper.Config;

internal class ConnectionSettings
{
  [YamlMember(Alias = "file", ApplyNamingConventions = false)]
  internal string? File { get; set; } = null;


  [YamlMember(Alias = "user", ApplyNamingConventions = false)]
  internal string? User { get; set; } = null;


  [YamlMember(Alias = "password", ApplyNamingConventions = false)]
  internal string? Password { get; set; } = null;


  [YamlMember(Alias = "pkey", ApplyNamingConventions = false)]
  internal string? PKey { get; set; } = null;

  [YamlMember(Alias = "connect-timeout", ApplyNamingConventions = false)]
  internal int? ConnectTimeout { get; set; } = null;

  [YamlMember(Alias = "read-timeout-ms", ApplyNamingConventions = false)]
  internal int? ReadTimeoutMs { get; set; } = null;
}

internal class Environment
{
  [YamlMember(Alias = "hosts", ApplyNamingConventions = false)]
  internal List<string> Hosts { get; set; } = null!;

  [YamlMember(Alias = "connection", ApplyNamingConventions = false)]
  public ConnectionSettings? ConnectionSettings { get; set; }
}

internal class SSH
{
  [YamlMember(Alias = "environments", ApplyNamingConventions = false)]
  public Dictionary<string, Environment>? Environments { get; set; }

  [YamlMember(Alias = "connection", ApplyNamingConventions = false)]
  internal ConnectionSettings ConnectionSettings { get; set; } = null!;
}

internal class Global
{
  [YamlMember(Alias = "ttl", ApplyNamingConventions = false)]
  internal int TTL { get; set; } = 60;

  [YamlMember(Alias = "background-resilience", ApplyNamingConventions = false)]
  internal int BackgroundResilience { get; set; } = 1;

  [YamlMember(Alias = "long-term-resilience", ApplyNamingConventions = false)]
  internal int LongTermResilience { get; set; } = 10;

  [YamlMember(Alias = "prefix", ApplyNamingConventions = false)]
  internal string? Prefix { get; set; } = null;

  [YamlMember(Alias = "histograms", ApplyNamingConventions = false)]
  public Dictionary<string, List<double>?>? Histograms { get; set; }

  [YamlMember(Alias = "format", ApplyNamingConventions = false)]
  public List<Dictionary<string, string>?> Format { get; set; } = null!;
}

internal class ScraperConfig
{
  [YamlMember(Alias = "global", ApplyNamingConventions = false)]
  public Global Global { get; set; } = null!;

  [YamlMember(Alias = "ssh", ApplyNamingConventions = false)]
  public SSH? SSH { get; set; }

  [YamlMember(Alias = "script", ApplyNamingConventions = false)]
  internal string? Script { get; set; } = null;

  [YamlMember(Alias = "reload-interval", ApplyNamingConventions = false)]
  internal int? ReloadInterval { get; set; } = null;
}
