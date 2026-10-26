#!/usr/bin/env bash
# DreamGrid installer for Debian 12 and 13 (amd64).
#
#   curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest/download/install.sh | sudo bash
#
# Options (with the one-liner: ... | sudo bash -s -- --web-port 8101):
#   --from <tarball>   install this dreamgrid-*-linux-x64.tar.gz instead of downloading the latest release
#   --web-port <n>     serve the admin website on port <n> instead of 8001 (1024-65535, not one of DreamGrid's own
#                      ports 3307, 8002, 8003, 8004, 8010, 8011); saved in the settings, so later upgrades keep it
#
# DREAMGRID_RELEASE=<tag> installs that release (for example a pre-release, which is never "latest") instead:
#   curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/download/v0.1.0-rc1/install.sh | sudo DREAMGRID_RELEASE=v0.1.0-rc1 bash
#
# Re-running it upgrades in place: the program in /opt/dreamgrid is replaced, the data in /var/lib/dreamgrid is never
# touched, and the world keeps running (the grid service and region restart briefly when a release updates OpenSim).
#
# Everything runs from main(), called on the last line: a download cut short defines nothing and so does nothing.
# Commands that might read stdin get </dev/null, since with the one-liner stdin is the rest of this script.
set -euo pipefail

releases=https://github.com/ystudy/DreamGrid-Opensim_Linux/releases
asset=dreamgrid-linux-x64.tar.gz
prefix=/opt/dreamgrid
# The private .NET runtime lives outside $prefix, one folder per version: children that keep running through an
# upgrade still load framework files from the runtime they started with.
runtimes=/opt/dreamgrid-runtime
data=/var/lib/dreamgrid
unit=/etc/systemd/system/dreamgrid.service
launcher=$prefix/launcher/DreamGrid.Launcher.dll

die() {
    echo "$*" >&2
    exit 1
}

os_release() {
    [ -r /etc/os-release ] || return 0
    sed -n "s/^$1=//p" /etc/os-release | tr -d '"' | head -n 1
}

# True when $1 is a web port the launcher accepts: 1024-65535 and none of DreamGrid's own default ports.
valid_web_port() {
    [[ $1 =~ ^[0-9]{1,5}$ ]] || return 1
    local n=$((10#$1))
    [ "$n" -ge 1024 ] && [ "$n" -le 65535 ] || return 1
    case "$n" in 3307 | 8002 | 8003 | 8004 | 8010 | 8011) return 1 ;; esac
}

# True once dreamgrid.service is active, waiting up to 30 s; a launcher that keeps failing never gets there.
wait_active() {
    local _
    for _ in $(seq 1 30); do
        systemctl is-active --quiet dreamgrid && return 0
        sleep 1
    done
    return 1
}

# True when a running process executes from, or has mapped a file under, directory $1.
in_use() {
    local proc
    for proc in /proc/[0-9]*; do
        case "$(readlink "$proc/exe" 2>/dev/null)" in "$1"/*) return 0 ;; esac
        grep -qF -- "$1/" "$proc/maps" 2>/dev/null && return 0
    done
    return 1
}

main() {
    local from="" web_port=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --from)
                [ $# -ge 2 ] || die "--from needs the path of a DreamGrid package."
                from=$2
                shift 2
                ;;
            --web-port)
                [ $# -ge 2 ] || die "--web-port needs a port number."
                web_port=$2
                shift 2
                ;;
            *) die "Unknown option: $1. Use --from <package> or --web-port <port>." ;;
        esac
    done
    if [ -n "$web_port" ]; then
        valid_web_port "$web_port" ||
            die "--web-port needs a port number from 1024 to 65535 that DreamGrid does not use itself (not 3307, 8002, 8003, 8004, 8010 or 8011)."
        web_port=$((10#$web_port))
    fi
    # The tag goes into a URL: only v<major>.<minor>.<patch> with an optional pre-release suffix.
    local release=$releases/latest/download
    if [ -n "${DREAMGRID_RELEASE:-}" ]; then
        [[ $DREAMGRID_RELEASE =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] ||
            die "DREAMGRID_RELEASE needs a release tag such as v1.2.3 or v1.2.3-rc1."
        release=$releases/download/$DREAMGRID_RELEASE
    fi

    # --- 1. Root and Debian 12/13 ---------------------------------------------------------------------------------
    [ "$(id -u)" -eq 0 ] || die "Please run the installer as root, for example with sudo."
    local os_id os_version os_name icu
    os_id=$(os_release ID)
    os_version=$(os_release VERSION_ID)
    os_name=$(os_release PRETTY_NAME)
    case "$os_id:$os_version" in
        debian:12) icu=libicu72 ;;
        debian:13) icu=libicu76 ;;
        *) die "DreamGrid installs on Debian 12 or 13 only. This system is ${os_name:-unknown}." ;;
    esac
    [ "$(dpkg --print-architecture)" = amd64 ] || die "DreamGrid needs a 64-bit PC (amd64) system."
    if [ -n "$from" ]; then
        [ -f "$from" ] || die "There is no file $from."
        from=$(realpath "$from")
    fi

    # --- 2. System packages (Debian's own mariadb.service is left as installed; DreamGrid runs its own server) ----
    echo "Installing the system packages DreamGrid needs..."
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q </dev/null
    apt-get install -y -q mariadb-server libgdiplus ca-certificates curl iproute2 "$icu" </dev/null

    # --- 3. The package --------------------------------------------------------------------------------------------
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    if [ -z "$from" ]; then
        echo "Downloading the ${DREAMGRID_RELEASE:-latest} DreamGrid release..."
        curl -fsSL -o "$work/$asset" "$release/$asset"
        curl -fsSL -o "$work/SHA256SUMS" "$release/SHA256SUMS"
        local expected actual
        expected=$(awk -v name="$asset" '$2 == name || $2 == "*" name { print $1 }' "$work/SHA256SUMS")
        [ -n "$expected" ] || die "The release's SHA256SUMS does not list $asset."
        actual=$(sha256sum "$work/$asset" | awk '{ print $1 }')
        [ "$actual" = "$expected" ] || die "The download is damaged (its SHA-256 does not match SHA256SUMS). Please try again."
        from=$work/$asset
    fi

    # --- 4. The web port -------------------------------------------------------------------------------------------
    # The port lives in the settings; an upgrade keeps it. An older unit pinned --web-port on ExecStart, where the
    # launcher re-applied it on every start: that port is saved once more (step 7) and never written to the unit again.
    local pinned=""
    if [ -f "$unit" ]; then
        pinned=$(grep -E '^ExecStart=' "$unit" | grep -oE -- '--web-port [0-9]+' | awk '{ print $2 }' | head -n 1 || true)
    fi
    if [ -z "$web_port" ] && [ -n "$pinned" ] && valid_web_port "$pinned"; then
        web_port=$((10#$pinned))
    fi
    local current
    current=$(sed -nE 's/^[[:space:]]*"webPort":[[:space:]]*([0-9]+).*/\1/p' "$data/settings.json" 2>/dev/null | head -n 1 || true)
    current=${current:-8001}
    local port=${web_port:-$current}
    # A running DreamGrid holds its own port; any other port it is about to move to must be free.
    if { ! systemctl is-active --quiet dreamgrid || [ "$port" != "$current" ]; } && [ -n "$(ss -ltnH "sport = :$port")" ]; then
        die "Port $port is already used by another program. Re-run with --web-port <free port>, for example --web-port 8101."
    fi

    # --- 5. Service user and data directory ------------------------------------------------------------------------
    if ! id -u dreamgrid >/dev/null 2>&1; then
        useradd --system --home-dir "$data" --shell /usr/sbin/nologin dreamgrid
    fi
    if [ ! -d "$data" ]; then
        install -d -o dreamgrid -g dreamgrid -m 0700 "$data"
    fi

    # --- 6. Program files: unpack beside the old ones, then swap; the runtime goes to its own versioned folder -----
    echo "Unpacking DreamGrid..."
    rm -rf "$prefix.new"
    mkdir -p "$prefix.new"
    tar -xzf "$from" -C "$prefix.new" --strip-components=1 --no-same-owner ||
        die "$from could not be unpacked. Is it a DreamGrid package?"
    local required
    for required in VERSION RUNTIME_VERSION launcher/DreamGrid.Launcher.dll dotnet/dotnet setup/debian/dreamgrid.service; do
        [ -f "$prefix.new/$required" ] || die "$from is not a DreamGrid package (it has no $required)."
    done
    chown -R root:root "$prefix.new"
    chmod -R go-w "$prefix.new"
    chmod 755 "$prefix.new"
    local new_version runtime_version runtime dotnet
    new_version=$(head -n 1 "$prefix.new/VERSION")
    runtime_version=$(head -n 1 "$prefix.new/RUNTIME_VERSION")
    [[ $runtime_version =~ ^[0-9A-Za-z][0-9A-Za-z.+-]*$ ]] || die "$from has an unreadable RUNTIME_VERSION."
    runtime=$runtimes/$runtime_version
    dotnet=$runtime/dotnet
    if [ ! -d "$runtime" ]; then
        install -d -m 755 "$runtimes"
        rm -rf "$runtime.new"
        cp -a "$prefix.new/dotnet" "$runtime.new"
        mv -T "$runtime.new" "$runtime"
    fi

    local old_version=""
    if [ -e "$prefix" ]; then
        if [ -f "$prefix/VERSION" ]; then old_version=$(head -n 1 "$prefix/VERSION"); fi
        # Only the launcher stops (KillMode=process); the world keeps running from its copy in the data directory.
        systemctl stop dreamgrid 2>/dev/null || true
        rm -rf "$prefix.old"
        mv -T "$prefix" "$prefix.old"
    fi
    mv -T "$prefix.new" "$prefix"

    # --- 7. The service --------------------------------------------------------------------------------------------
    local template=$prefix/setup/debian/dreamgrid.service
    sed -e "/^#/! s|/opt/dreamgrid/dotnet|$runtime|g" "$template" >"$unit"
    chmod 644 "$unit"
    systemctl daemon-reload
    systemctl enable --quiet dreamgrid
    # The port is saved in the settings, once: set-web-port takes the data directory's lock, so the launcher must not
    # run (an upgrade stopped it above). A refused port leaves the settings as they were; the service still starts.
    local port_error=""
    if [ -n "$web_port" ]; then
        systemctl stop dreamgrid 2>/dev/null || true
        if port_error=$(runuser -u dreamgrid -- "$dotnet" "$launcher" set-web-port "$web_port" --data "$data" </dev/null 2>&1); then
            port_error=""
        fi
    fi
    # restart also starts a stopped service. A launcher that fails at once (a taken port, say) restarts every few
    # seconds and is never active: that is a failed install, not a successful one.
    systemctl restart dreamgrid || true
    wait_active || die "DreamGrid did not start. See what happened with: journalctl -u dreamgrid"
    if [ -n "$old_version" ]; then
        echo "Upgraded DreamGrid $old_version → $new_version."
    else
        echo "Installed DreamGrid $new_version."
    fi

    # Older runtimes go once nothing runs on them; a region adopted through the upgrade may still use one.
    local old
    for old in "$runtimes"/*; do
        if [ ! -d "$old" ] || [ "$old" = "$runtime" ]; then continue; fi
        if ! in_use "$old"; then rm -rf "$old"; fi
    done

    # --- 8. The link -----------------------------------------------------------------------------------------------
    local link
    if ! link=$(runuser -u dreamgrid -- "$dotnet" "$launcher" admin-link --data "$data" --wait 60 </dev/null); then
        echo "$link" >&2
        die "DreamGrid did not start in time. See what happened with: journalctl -u dreamgrid"
    fi
    echo
    case "$link" in
        */setup\?token=*) echo "Open this link in your browser to set up your world:" ;;
        *) echo "Your world's admin page:" ;;
    esac
    echo "  $link"
    if [ -n "$port_error" ]; then
        echo "$port_error" >&2
        die "The admin website could not be moved to port $web_port; it still uses its previous port."
    fi
}

main "$@"
