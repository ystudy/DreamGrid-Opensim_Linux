#!/usr/bin/env bash
# DreamGrid installer for Debian 12 and 13 (amd64).
#
#   curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest/download/install.sh | sudo bash
#
# Options (with the one-liner: ... | sudo bash -s -- --web-port 8101):
#   --from <tarball>   install this dreamgrid-*-linux-x64.tar.gz instead of downloading the latest release
#   --web-port <n>     serve the admin website on port <n> instead of 8001
#
# DREAMGRID_RELEASE=<tag> installs that release (for example a pre-release, which is never "latest") instead:
#   curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/download/v0.1.0-rc1/install.sh | sudo DREAMGRID_RELEASE=v0.1.0-rc1 bash
#
# Re-running it upgrades in place: the program in /opt/dreamgrid is replaced, the data in /var/lib/dreamgrid is never
# touched, and the running world (database, grid service, region) keeps running through the upgrade.
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
        { [[ $web_port =~ ^[0-9]+$ ]] && [ "$web_port" -ge 1 ] && [ "$web_port" -le 65535 ]; } ||
            die "--web-port needs a port number between 1 and 65535."
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
    # An upgrade keeps the port the service was installed with.
    if [ -z "$web_port" ] && [ -f "$unit" ]; then
        web_port=$(grep -oE -- '--web-port [0-9]+' "$unit" | awk '{ print $2 }' || true)
    fi
    local port=${web_port:-8001}
    if ! systemctl is-active --quiet dreamgrid && [ -n "$(ss -ltnH "sport = :$port")" ]; then
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
    if [ -n "$web_port" ]; then
        sed -e "/^#/! s|/opt/dreamgrid/dotnet|$runtime|g" -e "/^ExecStart=/ s/\$/ --web-port $web_port/" "$template" >"$unit"
    else
        sed -e "/^#/! s|/opt/dreamgrid/dotnet|$runtime|g" "$template" >"$unit"
    fi
    chmod 644 "$unit"
    systemctl daemon-reload
    systemctl enable --quiet dreamgrid
    # restart also starts a stopped service.
    systemctl restart dreamgrid
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
}

main "$@"
