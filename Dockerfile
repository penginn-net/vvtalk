FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY VVTalk.csproj ./
RUN dotnet restore
COPY *.cs ./
RUN dotnet publish -c Release -o /app
RUN apt-get update && apt-get install -y --no-install-recommends unzip \
 && curl -fsSL -o /tmp/jdave.jar https://repo1.maven.org/maven2/club/minnced/jdave-native-linux-x86-64/0.1.7/jdave-native-linux-x86-64-0.1.7.jar \
 && unzip -j /tmp/jdave.jar natives/linux-x86-64/libdave.so -d /

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
# opus / libsodium は apt から入れ、Discord.Net が読むライブラリ名 (libopus.so / libsodium.so) に合わせてシンボリックリンクを張る
RUN apt-get update \
 && apt-get install -y --no-install-recommends libopus0 libsodium23 \
 && ln -s /usr/lib/x86_64-linux-gnu/libopus.so.0 /usr/lib/x86_64-linux-gnu/libopus.so \
 && ln -s /usr/lib/x86_64-linux-gnu/libsodium.so.23 /usr/lib/x86_64-linux-gnu/libsodium.so \
 && rm -rf /var/lib/apt/lists/*
# libdave は apt パッケージがないため、Java版で使っていた jdave のネイティブ配布物 (Maven Central) から取り出す
COPY --from=build /libdave.so /usr/lib/x86_64-linux-gnu/libdave.so
COPY --from=build /app ./
CMD ["dotnet", "VVTalk.dll"]
