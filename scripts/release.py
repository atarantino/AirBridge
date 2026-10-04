"""Version-gated release planning, artifact inspection and guarded publication.

Only the publish command mutates GitHub. Tests use fixture files and mocked API calls.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
INSTALLERS = ("AirBridge.msi", "AirBridge-Setup.exe")
REQUIRED_LIFECYCLE = {"clean-msi-install", "installed-host-ping", "clean-start-menu-launch",
                      "bootstrapper-ui-install", "bundle-start-menu-launch", "prior-version-upgrade",
                      "upgrade-settings-preservation", "downgrade-rejection", "repair", "final-uninstall"}


def version(text):
    if not re.fullmatch(r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)", text):
        raise ValueError(f"Unsupported release version: {text}")
    parts = tuple(map(int, text.split(".")))
    if parts[0] > 255 or parts[1] > 255 or parts[2] > 65535:
        raise ValueError("Release version exceeds Windows Installer limits")
    return parts


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write_json(path, value):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def gh(*args):
    result = subprocess.run(["gh", *args], text=True, capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError(result.stderr.strip())
    return result.stdout


def releases(repository):
    pages = json.loads(gh("api", f"repos/{repository}/releases?per_page=100", "--paginate", "--slurp"))
    return [item for page in pages for item in page]


def plan_release(current, published):
    target = version(current)
    stable = []
    for release in published:
        if release["draft"] or release["prerelease"]:
            continue
        tag = release["tag_name"]
        stable.append((version(tag.removeprefix("v")), release))
    if any(number == target for number, _ in stable):
        return {"release": False, "version": current, "tag": "v" + current}
    if not stable:
        raise ValueError("A prior stable release is required for upgrade validation")
    prior_number, prior = max(stable, key=lambda item: item[0])
    if target <= prior_number:
        raise ValueError("Refusing to publish a version below the newest stable release")
    if any(item["tag_name"].removeprefix("v") == current for item in published):
        raise ValueError("A draft/prerelease already reserves this version; resolve it before retrying")
    assets = [asset for asset in prior["assets"] if asset["name"] == "AirBridge.msi"]
    if len(assets) != 1 or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", assets[0].get("digest") or ""):
        raise ValueError("Prior stable MSI is missing or has no SHA-256 digest")
    return {"release": True, "version": current, "tag": "v" + current,
            "priorRelease": {"tag": prior["tag_name"], "version": ".".join(map(str, prior_number)),
                             "sha256": assets[0]["digest"][7:].lower()}}


def require_passed(report, required, commit=None):
    passed = {check["name"] for check in report["checks"] if check["status"] == "passed"}
    if report["status"] != "passed" or not required <= passed:
        raise ValueError("Required validation did not pass: " + ", ".join(sorted(required - passed)))
    if commit and (report["commit"] != commit or report["dirty"]):
        raise ValueError("Build report is not from the exact clean release commit")


def payload_files(xml_path, extracted):
    document = ET.parse(xml_path)
    ns = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
    install = document.find(".//w:Directory[@Id='INSTALLFOLDER']", ns)
    if install is None:
        raise ValueError("MSI installation directory is missing")
    files = []

    def visit(node, relative):
        for child in node:
            kind = child.tag.rsplit("}", 1)[-1]
            if kind == "Directory":
                name = child.attrib["Name"]
                if name in (".", "..") or "/" in name or "\\" in name:
                    raise ValueError("Unsafe MSI directory")
                visit(child, relative / name)
            elif kind == "File":
                name = child.attrib["Name"]
                if Path(name).name != name or "\\" in name or name in (".", ".."):
                    raise ValueError("Unsafe MSI filename")
                file_id = child.attrib["Id"]
                if not re.fullmatch(r"[A-Za-z0-9_.]+", file_id):
                    raise ValueError("Unsafe MSI file identifier")
                content = Path(extracted) / "File" / file_id
                files.append({"file": (relative / name).as_posix(), "sha256": sha256(content)})
            else:
                visit(child, relative)

    visit(install, Path())
    names = {item["file"] for item in files}
    if len(names) != len(files) or not {"AirBridge.App.exe", "RaopHost/AirBridge.RaopHost.exe"} <= names:
        raise ValueError("MSI payload lacks the app/host or has duplicate destinations")
    return document.find("w:Package", ns).attrib["Version"], files


def make_manifest(directory, package_report, xml_path, extracted, plan):
    commit = plan["sourceCommit"]
    require_passed(package_report, {"verify", "packaged-host-ping", "msi-build", "installer-build"}, commit)
    if package_report["productVersion"] != plan["version"]:
        raise ValueError("Package version does not match release plan")
    package_version, payload = payload_files(xml_path, extracted)
    if package_version != plan["version"]:
        raise ValueError("Actual MSI version does not match release plan")
    assets = [{"name": name, "bytes": (Path(directory) / name).stat().st_size,
               "sha256": sha256(Path(directory) / name)} for name in INSTALLERS]
    recorded = {Path(item["path"]).name: item["sha256"] for item in package_report["artifactHashes"]}
    if any(recorded.get(item["name"]) != item["sha256"] for item in assets):
        raise ValueError("Installer changed after packaging")
    return {"version": plan["version"], "tag": plan["tag"], "sourceCommit": commit,
            "ciRunId": plan["ciRunId"], "priorRelease": plan["priorRelease"], "assets": assets,
            "packageInspection": {"msiPayloadFiles": payload}}


def validate_candidate(directory, lifecycle):
    directory = Path(directory)
    manifest = read_json(directory / "release-validation.json")
    version(manifest["version"])
    if manifest["tag"] != "v" + manifest["version"] or not re.fullmatch(r"[0-9a-f]{40}", manifest["sourceCommit"]):
        raise ValueError("Invalid candidate version/tag/commit")
    require_passed(lifecycle, REQUIRED_LIFECYCLE)
    if lifecycle["artifactSourceCommit"] != manifest["sourceCommit"]:
        raise ValueError("Installer lifecycle checked a different commit")
    if lifecycle["priorMsiSha256"] != manifest["priorRelease"]["sha256"]:
        raise ValueError("Installer lifecycle used a different upgrade baseline")
    tested = {item["name"]: item["sha256"] for item in lifecycle["artifactHashes"]}
    if {item["name"] for item in manifest["assets"]} != set(INSTALLERS):
        raise ValueError("Unexpected installer asset list")
    for asset in manifest["assets"]:
        if sha256(directory / asset["name"]) != asset["sha256"] or tested.get(asset["name"]) != asset["sha256"]:
            raise ValueError("Published installer differs from the tested bytes")
    return manifest


def verify_uploaded_draft(directory, manifest, repository):
    directory = Path(directory)
    # The tag REST endpoint cannot retrieve an unpublished draft. The CLI resolves
    # drafts for authenticated users and returns their stable release ID URL.
    metadata = json.loads(gh("release", "view", manifest["tag"], "--repo", repository, "--json", "apiUrl"))
    prefix = f"https://api.github.com/repos/{repository}/releases/"
    api_url = metadata["apiUrl"]
    if not api_url.startswith(prefix) or not api_url[len(prefix):].isdigit():
        raise ValueError("Unexpected draft release API URL")
    uploaded = json.loads(gh("api", api_url))
    names = [*INSTALLERS, "release-validation.json", "installer-lifecycle-validation.json", "SHA256SUMS.txt"]
    expected = {name: "sha256:" + sha256(directory / name) for name in names}
    if not uploaded["draft"] or uploaded["tag_name"] != manifest["tag"] or \
            uploaded["target_commitish"] != manifest["sourceCommit"] or \
            {asset["name"]: asset.get("digest") for asset in uploaded["assets"]} != expected:
        raise ValueError("Draft assets or target do not match the tested candidate; leaving draft unpublished")


def publish(directory, lifecycle_path, repository, expected_commit):
    directory = Path(directory)
    manifest = validate_candidate(directory, read_json(lifecycle_path))
    if manifest["sourceCommit"] != expected_commit:
        raise ValueError("Candidate differs from the successful CI commit")
    decision = plan_release(manifest["version"], releases(repository))
    if not decision["release"]:
        print("Version already published; leaving existing release untouched")
        return
    # Never use an existing tag for a different commit, including annotated tags.
    refs = gh("api", f"repos/{repository}/git/matching-refs/tags/{manifest['tag']}")
    for ref in json.loads(refs):
        if ref["ref"] != "refs/tags/" + manifest["tag"]:
            continue
        obj = ref["object"]
        while obj["type"] == "tag":
            obj = json.loads(gh("api", f"repos/{repository}/git/tags/{obj['sha']}"))["object"]
        if obj["type"] != "commit" or obj["sha"] != expected_commit:
            raise ValueError("Release tag already points to a different commit")
    report_name = "installer-lifecycle-validation.json"
    write_json(directory / report_name, read_json(lifecycle_path))
    names = [*INSTALLERS, "release-validation.json", report_name]
    (directory / "SHA256SUMS.txt").write_text(
        "".join(f"{sha256(directory / name)}  {name}\n" for name in names), encoding="utf-8")
    gh("release", "create", manifest["tag"], "--repo", repository, "--target", expected_commit,
       "--title", "AirBridge for Windows " + manifest["tag"], "--generate-notes", "--draft",
       *(str(directory / name) for name in [*names, "SHA256SUMS.txt"]))
    # A failed upload stays private; only a complete, verified upload becomes an update.
    verify_uploaded_draft(directory, manifest, repository)
    gh("release", "edit", manifest["tag"], "--repo", repository, "--draft=false", "--latest")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    plan = sub.add_parser("plan")
    plan.add_argument("--repository", required=True)
    plan.add_argument("--commit", required=True)
    plan.add_argument("--ci-run", required=True)
    plan.add_argument("--output", required=True)
    manifest = sub.add_parser("manifest")
    for arg in ("directory", "package-report", "xml", "extracted", "plan"):
        manifest.add_argument("--" + arg, required=True)
    prior = sub.add_parser("verify-prior")
    prior.add_argument("--directory", required=True)
    prior.add_argument("--msi", required=True)
    release = sub.add_parser("publish")
    for arg in ("directory", "lifecycle", "repository", "commit"):
        release.add_argument("--" + arg, required=True)
    args = parser.parse_args()
    if args.command == "plan":
        current = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/Version")
        decision = plan_release(current, releases(args.repository))
        decision.update(sourceCommit=args.commit, ciRunId=args.ci_run)
        write_json(args.output, decision)
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"release={str(decision['release']).lower()}\ntag={decision['tag']}\n")
            if decision["release"]:
                output.write(f"prior_tag={decision['priorRelease']['tag']}\nprior_version={decision['priorRelease']['version']}\n")
    elif args.command == "manifest":
        value = make_manifest(args.directory, read_json(args.package_report), args.xml, args.extracted, read_json(args.plan))
        write_json(Path(args.directory) / "release-validation.json", value)
    elif args.command == "verify-prior":
        manifest = read_json(Path(args.directory) / "release-validation.json")
        if sha256(args.msi) != manifest["priorRelease"]["sha256"]:
            raise ValueError("Downloaded prior MSI does not match GitHub's SHA-256 digest")
    elif args.command == "publish":
        publish(args.directory, args.lifecycle, args.repository, args.commit)


if __name__ == "__main__":
    main()
