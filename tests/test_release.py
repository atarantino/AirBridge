import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("airbridge_release", ROOT / "scripts/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
COMMIT = "a" * 40


def stable(tag="v1.0.3", **changes):
    result = {"tag_name": tag, "draft": False, "prerelease": False,
              "assets": [{"name": "AirBridge.msi", "digest": "sha256:" + "b" * 64}]}
    result.update(changes)
    return result


class ReleaseTests(unittest.TestCase):
    def test_new_version_uses_newest_stable_not_prerelease_or_draft(self):
        plan = release.plan_release("1.0.4", [stable("v1.0.2"), stable(),
            stable("v9.0.0", prerelease=True), stable("v8.0.0", draft=True)])
        self.assertTrue(plan["release"])
        self.assertEqual("v1.0.3", plan["priorRelease"]["tag"])

    def test_same_version_is_noop_including_unprefixed_tags(self):
        for tag in ("v1.0.3", "1.0.3"):
            self.assertFalse(release.plan_release("1.0.3", [stable(tag)])["release"])

    def test_invalid_or_regressive_version_never_releases(self):
        for text in ("1.0.2", "1.0", "01.0.4", "1.0.4-beta", "256.0.0", "1.0.65536"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                release.plan_release(text, [stable()])

    def test_existing_draft_and_unverifiable_upgrade_baseline_fail_closed(self):
        for published in ([], [stable(), stable("v1.0.4", draft=True)],
                          [stable(assets=[])], [stable(assets=[{"name": "AirBridge.msi", "digest": None}])]):
            with self.subTest(published=published), self.assertRaises(ValueError):
                release.plan_release("1.0.4", published)

    def candidate(self, directory):
        plan = release.plan_release("1.0.4", [stable()])
        plan.update(sourceCommit=COMMIT, ciRunId="123")
        assets = []
        for name in release.INSTALLERS:
            (directory / name).write_bytes(name.encode())
            assets.append({"name": name, "bytes": len(name), "sha256": release.sha256(directory / name)})
        manifest = {**plan, "assets": assets}
        release.write_json(directory / "release-validation.json", manifest)
        lifecycle = {"status": "passed", "artifactSourceCommit": COMMIT,
                     "priorMsiSha256": "b" * 64, "artifactHashes": assets,
                     "checks": [{"name": name, "status": "passed"} for name in release.REQUIRED_LIFECYCLE]}
        path = directory / "lifecycle.json"
        release.write_json(path, lifecycle)
        return manifest, lifecycle, path

    def test_failed_skipped_missing_and_wrong_commit_lifecycle_prevent_publication(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            _, good, report = self.candidate(directory)
            cases = [{**good, "status": "failed"}, {**good, "checks": []},
                     {**good, "artifactSourceCommit": "c" * 40}, {**good, "priorMsiSha256": "c" * 64}]
            skipped = copy.deepcopy(good)
            skipped["checks"][0]["status"] = "skipped"
            cases.append(skipped)
            for bad in cases:
                release.write_json(report, bad)
                with patch.object(release, "gh") as gh, self.assertRaises(ValueError):
                    release.publish(directory, report, "owner/repo", COMMIT)
                gh.assert_not_called()

    def test_changed_installer_and_wrong_ci_commit_prevent_publication(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            _, _, report = self.candidate(directory)
            with patch.object(release, "gh") as gh, self.assertRaises(ValueError):
                release.publish(directory, report, "owner/repo", "c" * 40)
            gh.assert_not_called()
            (directory / "AirBridge-Setup.exe").write_bytes(b"changed after testing")
            with patch.object(release, "gh") as gh, self.assertRaises(ValueError):
                release.publish(directory, report, "owner/repo", COMMIT)
            gh.assert_not_called()

    def test_published_version_never_overwrites_existing_release(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            _, _, report = self.candidate(directory)
            with patch.object(release, "releases", return_value=[stable("v1.0.4")]), patch.object(release, "gh") as gh:
                release.publish(directory, report, "owner/repo", COMMIT)
            gh.assert_not_called()

    def test_conflicting_tag_prevents_release_creation(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            _, _, report = self.candidate(directory)
            conflict = json.dumps([{"ref": "refs/tags/v1.0.4", "object": {"type": "commit", "sha": "c" * 40}}])
            with patch.object(release, "releases", return_value=[stable()]), patch.object(release, "gh", return_value=conflict) as gh:
                with self.assertRaises(ValueError):
                    release.publish(directory, report, "owner/repo", COMMIT)
            self.assertEqual(1, gh.call_count)
            self.assertEqual("api", gh.call_args.args[0])

    def test_passed_exact_candidate_publishes_correct_commit_and_checksums(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            _, _, report = self.candidate(directory)
            def response(*args):
                if args[0:2] == ("release", "create"):
                    return "created draft"
                if args[0:2] == ("release", "edit"):
                    return "published"
                if "matching-refs" in args[1]:
                    return "[]"
                names = [*release.INSTALLERS, "release-validation.json", "installer-lifecycle-validation.json", "SHA256SUMS.txt"]
                return json.dumps({"draft": True, "target_commitish": COMMIT, "assets":
                    [{"name": name, "digest": "sha256:" + release.sha256(directory / name)} for name in names]})
            with patch.object(release, "releases", return_value=[stable()]), patch.object(release, "gh", side_effect=response) as gh:
                release.publish(directory, report, "owner/repo", COMMIT)
            create = gh.call_args_list[1].args
            self.assertEqual(("release", "create", "v1.0.4"), create[:3])
            self.assertIn(COMMIT, create)
            self.assertIn("--draft", create)
            self.assertEqual(("release", "edit", "v1.0.4"), gh.call_args.args[:3])
            sums = (directory / "SHA256SUMS.txt").read_text()
            for name in (*release.INSTALLERS, "release-validation.json", "installer-lifecycle-validation.json"):
                self.assertIn(release.sha256(directory / name) + "  " + name, sums)

    def test_incomplete_github_upload_leaves_draft_unpublished(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            _, _, report = self.candidate(directory)
            incomplete = json.dumps({"draft": True, "target_commitish": COMMIT, "assets": []})
            with patch.object(release, "releases", return_value=[stable()]), patch.object(release, "gh", side_effect=["[]", "created", incomplete]) as gh:
                with self.assertRaises(ValueError):
                    release.publish(directory, report, "owner/repo", COMMIT)
            self.assertEqual(3, gh.call_count)
            self.assertFalse(any(call.args[:2] == ("release", "edit") for call in gh.call_args_list))

    def test_package_must_be_clean_exact_commit_with_all_build_checks(self):
        report = {"status": "passed", "commit": COMMIT, "dirty": False,
                  "checks": [{"name": "verify", "status": "passed"}]}
        release.require_passed(report, {"verify"}, COMMIT)
        for bad in ({**report, "dirty": True}, {**report, "commit": "b" * 40}, {**report, "checks": []}):
            with self.assertRaises(ValueError):
                release.require_passed(bad, {"verify"}, COMMIT)

    def test_manifest_hashes_actual_extracted_payload_and_rejects_changed_msi(self):
        with tempfile.TemporaryDirectory() as path:
            directory = Path(path)
            plan, _, _ = self.candidate(directory)
            extracted = directory / "extracted/File"
            extracted.mkdir(parents=True)
            (extracted / "app").write_bytes(b"app")
            (extracted / "host").write_bytes(b"host")
            xml = directory / "package.wxs"
            xml.write_text('''<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Package Version="1.0.4">
<Directory Id="INSTALLFOLDER"><Component><File Id="app" Name="AirBridge.App.exe"/></Component>
<Directory Name="RaopHost"><Component><File Id="host" Name="AirBridge.RaopHost.exe"/></Component></Directory>
</Directory></Package></Wix>''')
            report = {"status": "passed", "commit": COMMIT, "dirty": False, "productVersion": "1.0.4",
                      "checks": [{"name": name, "status": "passed"} for name in ("verify", "packaged-host-ping", "msi-build", "installer-build")],
                      "artifactHashes": [{"path": str(directory / asset["name"]), "sha256": asset["sha256"]} for asset in plan["assets"]]}
            manifest = release.make_manifest(directory, report, xml, extracted.parent, plan)
            self.assertEqual({"AirBridge.App.exe", "RaopHost/AirBridge.RaopHost.exe"},
                             {item["file"] for item in manifest["packageInspection"]["msiPayloadFiles"]})
            (directory / "AirBridge.msi").write_bytes(b"changed")
            with self.assertRaises(ValueError):
                release.make_manifest(directory, report, xml, extracted.parent, plan)


if __name__ == "__main__":
    unittest.main()
