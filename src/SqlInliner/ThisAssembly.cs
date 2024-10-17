// ReSharper disable once CheckNamespace
partial class ThisAssembly
{
#if DEBUG
    public const string AppName = Info.Product + " v" + Info.InformationalVersion + " - DEV";
#else
    public const string AppName = Info.Product + " v" + Info.InformationalVersion + " - " + Metadata.RepositoryUrl;
#endif
}