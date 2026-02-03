# WebsiteDownloader Discord Bot

A Discord bot that downloads static website assets (HTML/CSS/JS/images), beautifies the HTML, zips the result, uploads the zip back to Discord, and then cleans up local files.

## Requirements
- .NET SDK 8

## Run
```powershell
dotnet run --project WebsiteDownloader
```

## Notes
- The downloader only fetches static assets. It does not capture backend/server behavior.
- Results can be incomplete depending on how the site loads assets.
