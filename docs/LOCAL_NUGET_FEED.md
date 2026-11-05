# Install LogixTagRelay from a local NuGet feed

A local NuGet feed is a folder of `.nupkg` files. Visual Studio can browse it and install packages into your project. The interface ZIP ships your compiled contract and its source files. Keep the reusable NuGet packages in a separate feed. [Microsoft Learn on local feeds](https://learn.microsoft.com/en-us/nuget/hosting-packages/local-feeds)

## 1. Choose a feed folder

You can point Visual Studio directly to the demo's `packages` folder or to `artifacts/packages` after building the repository's packages. Use **Install Local NuGet Feed.cmd** if you want a separate, reusable feed on your PC. It copies the available LogixTagRelay `.nupkg` files into `%LOCALAPPDATA%\LogixTagRelay\NuGetFeed`, prints the path, and leaves package source registration to you. The command is shipped alongside the repository's packaged output and is also available in `scripts/`.

With the command next to the `.nupkg` files, double-click it. From the repository root, you can also point it to the demo packages or choose a different feed folder.

```powershell
& '.\scripts\Install Local NuGet Feed.cmd' -PackageDirectory "$env:USERPROFILE\Downloads\LogixTagRelay-Demo\packages"
```

To choose your own feed location, add `-FeedPath 'C:\NuGet\LogixTagRelay'`. Existing identical packages are kept. If a feed already contains different bytes for the same package filename, use a separate feed for that build or a new package version.

## 2. Register the source in Visual Studio

1. Open **Tools → Options → NuGet Package Manager → Package Sources**. You can also open these settings from the gear next to **Package source** in the NuGet window.
2. Add a source named `LogixTagRelay Local` and set its path to the feed folder from step 1. Save the settings.
3. Keep nuget.org or your organization's approved dependency feed enabled for third-party dependencies. If your solution uses package source mapping, map `LogixTagRelay` and `LogixTagRelay.*` to the local source.

For several developers, a shared folder can serve as the source. Register its path on each PC or use your solution's NuGet configuration. [Visual Studio package sources](https://learn.microsoft.com/en-us/nuget/consume-packages/install-use-packages-visual-studio#package-sources) · [Package source mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping)

## 3. Install packages into your project

1. In Solution Explorer, right-click the consuming project and choose **Manage NuGet Packages**.
2. Select `LogixTagRelay Local` as **Package source**, open **Browse**, and check **Include prerelease**.
3. Install `LogixTagRelay` and any transport packages you use. Match the version listed in the contract ZIP's `README.txt`. For the current preview, that version is `0.8.1-preview.1`.
4. Add the compiled contract DLL to the project using the [consumer manual](CONSUMER_MANUAL.md).

Visual Studio restores these packages into its package cache as it builds your project. The local feed remains the source for future restores. [Install packages in Visual Studio](https://learn.microsoft.com/en-us/nuget/consume-packages/install-use-packages-visual-studio#find-and-install-a-package)
