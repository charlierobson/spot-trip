# spot-trip

## Building
cd recorder
make
cd ../SpotifyRemote
dotnet build

## running
edit appsettings.json
copy appsettings.local.json.example appsettings.local.json
[[paste api secret and save]]
dotnet run
or
./bin/debug/net10.0/SpotifyRemote

## publishing
dotnet publish
=> ./publish
zip & distribute
