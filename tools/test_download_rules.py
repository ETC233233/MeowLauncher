"""Loader eligibility and API ordering; all metadata mocked, no user files changed."""
import importlib.util, sys, unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from ml2 import loaders
spec=importlib.util.spec_from_file_location('sidecar_download',Path(__file__).resolve().parents[1]/'MeowLauncher.WinUI/Backend/sidecar.py')
sidecar=importlib.util.module_from_spec(spec);spec.loader.exec_module(sidecar)
class DownloadRules(unittest.TestCase):
    def test_189_only_queries_forge(self):
        self.assertEqual([k for k in ('fabric','forge','neoforge','quilt') if loaders.can_query_loader('1.8.9',k)],['forge'])
    def test_old_quilt_skips_network(self):
        with patch.object(loaders,'_get_json',side_effect=AssertionError('no network')):
            self.assertEqual(loaders.available_versions_raw('1.8.9','quilt'),[])
    def test_sidecar_old_loader_skips_worker(self):
        with patch.object(loaders,'available_versions_raw',side_effect=AssertionError('no request')):
            self.assertEqual(sidecar.loader_versions('1.8.9','quilt')['versions'],[])
    def test_neoforge_boundary(self):
        self.assertFalse(loaders.can_query_loader('1.19.4','neoforge'))
        self.assertTrue(loaders.can_query_loader('1.20.1','neoforge'))
    def test_snapshot_index_membership(self):
        with patch.object(loaders,'_get_json',return_value=[{'version':'26.3-snapshot-1'}]):
            self.assertTrue(loaders.loader_game_supported('26.3-snapshot-1','fabric'))
            self.assertFalse(loaders.loader_game_supported('26.3-snapshot-2','fabric'))
    def test_index_failure_is_not_unsupported(self):
        with patch.object(loaders,'_get_json',return_value=None):
            with self.assertRaises(RuntimeError):loaders.loader_game_supported('1.20.1','quilt')
    def test_unlisted_release_skips_loader_query(self):
        with patch.object(loaders,'_get_json',return_value=[{'version':'1.20.1'}]) as request:
            self.assertEqual(loaders.available_versions_raw('1.20.2','quilt'),[])
            self.assertEqual(request.call_count,1)
            self.assertTrue(request.call_args.args[0].endswith('/versions/game'))
    def test_ancient_versions_skip(self):
        for game in ('b1.7.3','a1.2.6','rd-132211'):
            for kind in ('fabric','forge','neoforge','quilt'):self.assertFalse(loaders.can_query_loader(game,kind))
    def test_reject_incompatible_install_before_download(self):
        with patch('ml2.net.mojang_versions',side_effect=AssertionError('must not download')):
            with self.assertRaisesRegex(RuntimeError,'不支持'):sidecar.install_version('1.8.9','quilt')
    def test_forge_game_index_before_builds(self):
        with patch.object(loaders,'_get_json',return_value=['1.8.9','1.13.2']) as request:
            self.assertEqual(loaders.available_versions_raw('1.13','forge'),[])
            self.assertEqual(request.call_count,1)
            self.assertTrue(request.call_args.args[0].endswith('/forge/minecraft'))
    def test_legacy_forge_prerelease_index(self):
        with patch.object(loaders,'_get_json',return_value=['1.7.10_pre4']):
            self.assertTrue(loaders.loader_game_supported('1.7.10_pre4','forge'))
    def api_rows(self):
        return [{'id':v,'project_id':loaders.FABRIC_API[0],'version_number':v,'date_published':d,'loaders':['fabric'],'game_versions':['1.20.1']}
                for v,d in [('old','2024-01-01T00:00:00Z'),('new','2025-01-01T00:00:00Z'),('middle','2024-07-01T00:00:00Z')]]
    def test_fresh_api_descending(self):
        with patch.object(loaders,'_cache_get',return_value=None),patch.object(loaders,'_cache_put'),patch('ml2.net.modrinth_versions',return_value=self.api_rows()):
            self.assertEqual([r['id'] for r in loaders.api_mod_versions(loaders.FABRIC_API[0],'1.20.1')],['new','middle','old'])
    def test_cached_api_also_sorted_and_filtered(self):
        rows=self.api_rows()+[{'id':'wrong','project_id':'other'}]
        with patch.object(loaders,'_cache_get',return_value=rows),patch.object(loaders,'_cache_put'),patch('ml2.net.modrinth_versions',side_effect=AssertionError('cache hit')):
            self.assertEqual([r['id'] for r in loaders.api_mod_versions(loaders.FABRIC_API[0],'1.20.1')],['new','middle','old'])
    def test_quilt_not_using_fabric_api(self):
        with patch.object(loaders,'_cache_get',return_value=None),patch.object(loaders,'_cache_put'),patch('ml2.net.modrinth_versions',return_value=[]) as query:
            loaders.api_mod_versions(loaders.QUILTED_API[0],'1.20.1')
            query.assert_called_once_with(loaders.QUILTED_API[0],loaders=['quilt'],game_versions=['1.20.1'])
if __name__=='__main__':unittest.main(verbosity=2)
