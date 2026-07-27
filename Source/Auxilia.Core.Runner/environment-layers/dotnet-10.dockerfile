# Environment capability "dotnet-10": the .NET 10 SDK preinstalled for coding sessions.
# Merged into the image's existing dotnet root so the workflow host keeps running unchanged.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm /tmp/dotnet-install.sh \
 && dotnet --list-sdks
