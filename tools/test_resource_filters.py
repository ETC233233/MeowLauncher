"""Online filters and standalone file downloads, no user data or network."""
import importlib.util
from pathlib import Path
import sys, tempfile, unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ml2 import net
spec = importlib.util.spec_from_file_location('sidecar_resource_filters', Path(__file__).resolve().parents[1] / 'MeowLauncher.WinUI/Backend/sidecar.py')
sidecar = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sidecar)

class ResourceFilters(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pack = patch.object(sidecar, '_pack_context', return_value=({}, net, self.root))
        self.pack.start(); self.addCleanup(self.pack.stop)

    def test_manual_does_not_read_current_instance(self):
        with patch.object(sidecar, '_online_context', side_effect=AssertionError('current instance leaked')), patch.object(net, 'modrinth_versions', return_value=[]) as query:
            sidecar.online_files('modrinth', 'p', 'old-instance', 'mod', {'gameVersion':'1.20.1','loader':'forge'})
            query.assert_called_once_with('p', loaders=['forge'], game_versions=['1.20.1'])

    def test_empty_filter_is_explicitly_unrestricted(self):
        with patch.object(net, 'modrinth_versions', return_value=[]) as query:
            sidecar.online_files('modrinth', 'p', '', 'mod', {})
            query.assert_called_once_with('p', loaders=None, game_versions=None)

    def test_follow_keeps_instance_context(self):
        with patch.object(sidecar, '_online_context', return_value=({},net,self.root,'instance',self.root,'26.3','fabric')), patch.object(net, 'modrinth_versions', return_value=[]) as query:
            sidecar.online_files('modrinth', 'p', 'instance')
            query.assert_called_once_with('p', loaders=['fabric'], game_versions=['26.3'])

    def test_curseforge_uses_manual_filters(self):
        with patch.object(net,'cf_mod_files',return_value=[]) as query:
            sidecar.online_files('curseforge','123','old','mod',{'gameVersion':'1.21.1','loader':'neoforge'})
            query.assert_called_once_with(123,'1.21.1','neoforge',limit=50,key=None)

    def test_modpack_uses_manual_filters(self):
        with patch.object(net,'modrinth_versions',return_value=[]) as query:
            sidecar.online_files('modrinth','p','','modpack',{'gameVersion':'1.20.1','loader':'fabric'})
            query.assert_called_once_with('p',loaders=['fabric'],game_versions=['1.20.1'])

    def test_download_uses_unified_source_downloader(self):
        version={'id':'f','project_id':'p'}
        with patch.object(net,'modrinth_version',return_value=version), patch.object(net,'install_modrinth_version',return_value=[self.root/'test.jar']) as download:
            result=sidecar.download_online_file('modrinth','p','f',str(self.root))
            self.assertTrue(result['ok']);download.assert_called_once_with(version,self.root)

    def test_download_rejects_wrong_project(self):
        with patch.object(net,'modrinth_version',return_value={'project_id':'other'}), patch.object(net,'install_modrinth_version') as download:
            with self.assertRaises(RuntimeError):sidecar.download_online_file('modrinth','p','f',str(self.root))
            download.assert_not_called()

    def test_curseforge_exact_file_download(self):
        item={'id':2,'modId':1}
        with patch.object(net,'cf_mod_file',return_value=item),patch.object(net,'install_curseforge_file',return_value=self.root/'a.jar') as download:
            self.assertTrue(sidecar.download_online_file('curseforge','1','2',str(self.root))['ok'])
            download.assert_called_once_with(item,self.root)

    def test_reject_relative_destination(self):
        with self.assertRaises(RuntimeError):sidecar.download_online_file('modrinth','p','f','relative')

if __name__=='__main__':unittest.main(verbosity=2)
