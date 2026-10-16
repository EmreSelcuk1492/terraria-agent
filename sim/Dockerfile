# Real tModLoader client with no visible window: Xvfb gives it an in-memory screen, Mesa draws in software.
# Game files are bind-mounted read-only at run time (see run.sh), so the image holds only the OS side.
FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb xauth libgl1 libgl1-mesa-dri libglx-mesa0 libegl1 \
      libx11-6 libxext6 libxcursor1 libxi6 libxrandr2 libxss1 libxkbcommon0 libxinerama1 libasound2 \
      socat curl ca-certificates procps pulseaudio xdotool x11-apps imagemagick ffmpeg \
    && rm -rf /var/lib/apt/lists/*
ENV SDL_AUDIODRIVER=pulseaudio \
    LIBGL_ALWAYS_SOFTWARE=1 \
    DISPLAY=:99
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 8979
ENTRYPOINT ["/entrypoint.sh"]
