namespace ThunderstoreCLI.Configuration;

class DefaultConfig : EmptyConfig
{
    public override GeneralConfig? GetGeneralConfig()
    {
        return new GeneralConfig()
        {
            Repository = Defaults.REPOSITORY_URL
        };
    }

    public override PackageConfig GetPackageMeta()
    {
        return new PackageConfig()
        {
            ProjectConfigPath = Defaults.PROJECT_CONFIG_PATH,
            Namespace = "AuthorName",
            Name = "PackageName",
            VersionNumber = "0.0.1",
            Description = "Example mod description",
            WebsiteUrl = "",
            ContainsNsfwContent = false
        };
    }

    public override InitConfig GetInitConfig()
    {
        return new InitConfig()
        {
            Overwrite = false
        };
    }

    public override BuildConfig GetBuildConfig()
    {
        return new BuildConfig()
        {
            IconPath = "./icon.png",
            ReadmePath = "./README.md",
            OutDir = "./build",
            CopyPaths = new()
            {
                { new("./dist", "", false) }
            }
        };
    }

    public override PublishConfig GetPublishConfig()
    {
        return new PublishConfig()
        {
            File = null
        };
    }
}
