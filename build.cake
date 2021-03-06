#addin "nuget:?package=Cake.FileHelpers"
#addin "nuget:?package=Cake.Git"
#addin "nuget:?package=Cake.Incubator"
#tool "nuget:?package=GitVersion.CommandLine"
#tool "nuget:?package=xunit.runner.console"
#load buildhelpers.cake

using System.Text.RegularExpressions;
using System.Linq;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

var gitVersion = GitVersion();

var solutionDirectory = MakeAbsolute(Directory("./"));
var artifactsDirectory = solutionDirectory.Combine("artifacts");
var artifactsBinDirectory = artifactsDirectory.Combine("bin");
var artifactsBinNet45Directory = artifactsBinDirectory.Combine("net45");
var artifactsBinNetStandard15Directory = artifactsBinDirectory.Combine("netstandard1.5");
var artifactsDocsDirectory = artifactsDirectory.Combine("docs");
var artifactsDocsApiDocsDirectory = artifactsDocsDirectory.Combine("ApiDocs-" + gitVersion.LegacySemVer);
var artifactsDocsRefDocsDirectory = artifactsDocsDirectory.Combine("RefDocs-" + gitVersion.LegacySemVer);
var artifactsPackagesDirectory = artifactsDirectory.Combine("packages");
var docsDirectory = solutionDirectory.Combine("Docs");
var docsApiDirectory = docsDirectory.Combine("Api");
var srcDirectory = solutionDirectory.Combine("src");
var testsDirectory = solutionDirectory.Combine("tests");
var toolsDirectory = solutionDirectory.Combine("Tools");
var toolsHugoDirectory = toolsDirectory.Combine("Hugo");

var solutionFile = solutionDirectory.CombineWithFilePath("CSharpDriver.sln");
var solutionFullPath = solutionFile.FullPath;
var srcProjectNames = new[]
{
    "MongoDB.Bson",
    "MongoDB.Driver.Core",
    "MongoDB.Driver",
    "MongoDB.Driver.Legacy",
    "MongoDB.Driver.GridFS"
};

Task("Default")
    .IsDependentOn("TestAndPackage");

Task("TestAndPackage")
    .IsDependentOn("Test")
    .IsDependentOn("Package");    
   
Task("Clean")
    .Does( ()=> 
    {
        CleanDirectory(artifactsDirectory);
        CleanDirectories("./**/obj");
        CleanDirectories($"./**/bin/{configuration}");
    });

Task("Restore")
    .IsDependentOn("Clean")
    .Does(() => 
    {
        DotNetCoreRestore(solutionFullPath);
    });

Task("Build")
    .IsDependentOn("Restore")
    .Does(() =>
    {
       var settings = new DotNetCoreBuildSettings
       {
           NoRestore = true,
           Configuration = configuration,
           EnvironmentVariables = new Dictionary<string, string>
           {
               { "Version", gitVersion.SemVer },
               { "SourceRevisionId", gitVersion.Sha }
           }
        };
        DotNetCoreBuild(solutionFullPath, settings);
    });

Task("Test")
    .IsDependentOn("Build")
    .DoesForEach(
        GetFiles("./**/*.Tests.csproj")
        .Where(name => !name.ToString().Contains("Atlas")),
        testProject => 
    {
        DotNetCoreTest(
            testProject.FullPath,
            new DotNetCoreTestSettings {
                NoBuild = true,
                NoRestore = true,
                Configuration = configuration,
                ArgumentCustomization = args => args.Append("-- RunConfiguration.TargetPlatform=x64")
            }
        );
    });
    
Task("TestAtlasConnectivity")
    .IsDependentOn("Build")
    .DoesForEach(
        GetFiles("./**/AtlasConnectivity.Tests.csproj"),
        testProject => 
{
    DotNetCoreTest(
        testProject.FullPath,
        new DotNetCoreTestSettings {
            NoBuild = true,
            NoRestore = true,
            Configuration = configuration,
            ArgumentCustomization = args => args.Append("-- RunConfiguration.TargetPlatform=x64")
        }
    );
});

Task("Docs")
    .IsDependentOn("ApiDocs")
    .IsDependentOn("RefDocs");

Task("ApiDocs")
    .IsDependentOn("Build")
    .Does(() =>
    {
        EnsureDirectoryExists(artifactsDocsApiDocsDirectory);
        CleanDirectory(artifactsDocsApiDocsDirectory);

        var shfbprojFile = docsApiDirectory.CombineWithFilePath("CSharpDriverDocs.shfbproj");
        var preliminary = false; // TODO: compute
        MSBuild(shfbprojFile, new MSBuildSettings
            {
                Configuration = "Release"
            }
            .WithProperty("OutputPath", artifactsDocsApiDocsDirectory.ToString())
            .WithProperty("CleanIntermediate", "True")
            .WithProperty("Preliminary", preliminary ? "True" : "False")
            .WithProperty("HelpFileVersion", gitVersion.LegacySemVer)
        );

        var lowerCaseIndexFile = artifactsDocsApiDocsDirectory.CombineWithFilePath("index.html");
        var upperCaseIndexFile = artifactsDocsApiDocsDirectory.CombineWithFilePath("Index.html");
        MoveFile(upperCaseIndexFile, lowerCaseIndexFile);

        var apiDocsZipFileName = artifactsDocsApiDocsDirectory.GetDirectoryName() + "-html.zip";
        var apiDocsZipFile = artifactsDocsDirectory.CombineWithFilePath(apiDocsZipFileName);
        Console.WriteLine(apiDocsZipFile.FullPath);
        Zip(artifactsDocsApiDocsDirectory, apiDocsZipFile);

        var chmFile = artifactsDocsApiDocsDirectory.CombineWithFilePath("CSharpDriverDocs.chm");
        var artifactsDocsChmFile = artifactsDocsDirectory.CombineWithFilePath("CSharpDriverDocs.chm");
        CopyFile(chmFile, artifactsDocsChmFile);

        DeleteDirectory(artifactsDocsApiDocsDirectory, recursive: true);
    });

Task("RefDocs")
    .Does(() =>
    {
        EnsureDirectoryExists(toolsHugoDirectory);
        CleanDirectory(toolsHugoDirectory);

        var url = "https://github.com/spf13/hugo/releases/download/v0.13/hugo_0.13_windows_amd64.zip";
        var hugoZipFile = toolsHugoDirectory.CombineWithFilePath("hugo_0.13_windows_amd64.zip");
        DownloadFile(url, hugoZipFile);
        Unzip(hugoZipFile, toolsHugoDirectory);
        var hugoExe = toolsHugoDirectory.CombineWithFilePath("hugo_0.13_windows_amd64.exe");

        var landingDirectory = docsDirectory.Combine("landing");
        var landingPublicDirectory = landingDirectory.Combine("public");
        CleanDirectory(landingPublicDirectory);

        var processSettings = new ProcessSettings
        {
            WorkingDirectory = landingDirectory
        };
        StartProcess(hugoExe, processSettings);

        var referenceDirectory = docsDirectory.Combine("reference");
        var referencePublicDirectory = referenceDirectory.Combine("public");
        CleanDirectory(referencePublicDirectory);

        processSettings = new ProcessSettings
        {
            WorkingDirectory = referenceDirectory
        };
        StartProcess(hugoExe, processSettings);

        EnsureDirectoryExists(artifactsDocsRefDocsDirectory);
        CleanDirectory(artifactsDocsRefDocsDirectory);

        CopyDirectory(landingPublicDirectory, artifactsDocsRefDocsDirectory);

        var artifactsReferencePublicDirectory = artifactsDocsRefDocsDirectory.Combine(gitVersion.Major + "." + gitVersion.Minor);
        CopyDirectory(referencePublicDirectory, artifactsReferencePublicDirectory);

        var refDocsZipFileName = artifactsDocsRefDocsDirectory.GetDirectoryName() + "-html.zip";
        var refDocsZipFile = artifactsDocsDirectory.CombineWithFilePath(refDocsZipFileName);
        Zip(artifactsDocsRefDocsDirectory, refDocsZipFile);

        DeleteDirectory(artifactsDocsRefDocsDirectory, recursive: true);
    });

Task("Package")
    .IsDependentOn("PackageReleaseZipFile")
    .IsDependentOn("PackageNugetPackages");

Task("PackageReleaseZipFile")
    .IsDependentOn("Build")
    .IsDependentOn("ApiDocs")
    .Does(() =>
    {
        var assemblySemVer = gitVersion.AssemblySemVer; // e.g. 2.4.4.0

        var stagingDirectoryName = "CSharpDriver-" + gitVersion.LegacySemVer;
        var stagingDirectory = artifactsDirectory.Combine(stagingDirectoryName);
        EnsureDirectoryExists(stagingDirectory);
        CleanDirectory(stagingDirectory);

        var stagingNet45Directory = stagingDirectory.Combine("net45");
        CopyDirectory(artifactsBinNet45Directory, stagingNet45Directory);
        DeleteFile(stagingNet45Directory.CombineWithFilePath("DnsClient.dll"));
        DeleteFile(stagingNet45Directory.CombineWithFilePath("DnsClient.xml"));

        var stagingNetStandard15Directory = stagingDirectory.Combine("netstandard1.5");
        CopyDirectory(artifactsBinNetStandard15Directory, stagingNetStandard15Directory);

        var chmFile = artifactsDocsDirectory.CombineWithFilePath("CSharpDriverDocs.chm");
        var stagingChmFileName = stagingDirectoryName + ".chm";
        var stagingChmFile = stagingDirectory.CombineWithFilePath(stagingChmFileName);
        CopyFile(chmFile, stagingChmFile);

        var licenseFile = solutionDirectory.CombineWithFilePath("license.txt");
        var stagingLicenseFile = stagingDirectory.CombineWithFilePath("license.txt");
        CopyFile(licenseFile, stagingLicenseFile);

        var releaseNotesFileName = "Release Notes v" + gitVersion.LegacySemVer + ".md";
        var releaseNotesDirectory = solutionDirectory.Combine("Release Notes");
        var releaseNotesFile =  releaseNotesDirectory.CombineWithFilePath(releaseNotesFileName);
        var stagingDirectoryReleaseNotesFile = stagingDirectory.CombineWithFilePath(releaseNotesFileName);
        CopyFile(releaseNotesFile, stagingDirectoryReleaseNotesFile);

        var zipFileName = stagingDirectoryName + ".zip";
        var zipFile = artifactsDirectory.CombineWithFilePath(zipFileName);
        Zip(stagingDirectory, zipFile);

        DeleteDirectory(stagingDirectory, recursive: true);
    });

Task("PackageNugetPackages")
    .IsDependentOn("Build")
    .Does(() =>
    {
        EnsureDirectoryExists(artifactsPackagesDirectory);
        CleanDirectory(artifactsPackagesDirectory);

        var projects = new[]
        {
            "MongoDB.Bson",
            "MongoDB.Driver.Core",
            "MongoDB.Driver",
            "MongoDB.Driver.GridFS",
            "MongoDB.Driver.Legacy"
        };

        foreach (var project in projects)
        {
            var projectPath = $"{srcDirectory}\\{project}\\{project}.csproj";
            var settings = new DotNetCorePackSettings
            {
                Configuration = configuration,
                OutputDirectory = artifactsPackagesDirectory
            };
            DotNetCorePack(projectPath, settings);
        }
    });

Task("PushToMyget")
    .Does(() =>
    {
        var mygetApiKey = EnvironmentVariable("MYGETAPIKEY");
        if (mygetApiKey == null)
        {
            throw new Exception("MYGETAPIKEY environment variable missing");
        }

        var packageFiles = new List<FilePath>();

        var nuspecFiles = GetFiles("./artifacts/packages/*.nuspec");
        foreach (var nuspecFile in nuspecFiles)
        {
            var packageFileName = nuspecFile.GetFilenameWithoutExtension() + ".nupkg";
            var packageFile = artifactsPackagesDirectory.CombineWithFilePath(packageFileName);
            packageFiles.Add(packageFile);
        }

        NuGetPush(packageFiles, new NuGetPushSettings
        {
            ApiKey = mygetApiKey,
            Source = "https://www.myget.org/F/mongodb/api/v2/package"
        });
    });

Task("DumpGitVersion")
    .Does(() =>
    {
        Information(gitVersion.Dump());
    });

RunTarget(target);
