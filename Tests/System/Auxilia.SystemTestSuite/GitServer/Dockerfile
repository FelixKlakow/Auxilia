# A minimal authenticated git-over-HTTP server for system tests: Apache + git-http-backend
# (smart HTTP) behind Basic auth, seeded with one bare repo containing README.md. The runner must
# present the credential (builduser / the-pat) — a wrong or missing credential gets a 401, so this
# proves the per-run repository's auth was resolved and injected correctly. Debian base: its git
# package ships git-http-backend (Alpine's does not).
FROM httpd:2.4

RUN apt-get update \
 && apt-get install -y --no-install-recommends git \
 && rm -rf /var/lib/apt/lists/* \
 && git config --global user.email seed@auxilia.local \
 && git config --global user.name seed \
 && git config --global init.defaultBranch main \
 && mkdir -p /tmp/seed && cd /tmp/seed && git init -q \
 && printf 'auxilia system test repo\n' > README.md \
 && git add README.md && git commit -q -m seed \
 && git clone -q --bare /tmp/seed /git/test.git \
 && git -C /git/test.git update-server-info \
 && rm -rf /tmp/seed && cd / \
 && mkdir -p /git-cgi \
 && cp "$(git --exec-path)/git-http-backend" /git-cgi/git-http-backend \
 && git config --system --add safe.directory '*' \
 && /usr/local/apache2/bin/htpasswd -bc /git.passwd builduser the-pat

# Build context is the repo root (see CoreApiDispatchEnvironment.BuildImageAsync).
COPY Tests/System/Auxilia.SystemTestSuite/GitServer/httpd.conf /usr/local/apache2/conf/httpd.conf
