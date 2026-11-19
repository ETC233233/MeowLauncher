"""Pack installation identity and stages; isolated files and mocked downloads only."""
import importlib.util, json, sys, tempfile, unittest, zipfile
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from ml2 import pack_status, modpack, core, loaders, net
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('sidecar_pack_status',ROOT/'MeowLauncher.WinUI/Backend/sidecar.py')
sidecar=importlib.util.module_from_spec(spec);spec.loader.exec_module(sidecar)

class Status(unittest.TestCase):
    def setUp(self):self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
    def instance(self,root,name='same'):
        p=root/'versions'/name;p.mkdir(parents=True);(p/(name+'.json')).write_text(json.dumps({'id':name,'mainClass':'test.Main','meowlauncher':{'existing':True}}));return p
    def test_record_preserves_profile(self):
        p=self.instance(self.root);pack_status.record(p,'modrinth','project','file')
        data=json.loads((p/'same.json').read_text());self.assertEqual(data['mainClass'],'test.Main');self.assertTrue(data['meowlauncher']['existing'])
        self.assertEqual(pack_status.installed([self.root],'modrinth','project')['file'][0]['id'],'same')
    def test_project_and_source_are_separate(self):
        p=self.instance(self.root);pack_status.record(p,'modrinth','project','file')
        self.assertEqual(pack_status.installed([self.root],'curseforge','project'),{});self.assertEqual(pack_status.installed([self.root],'modrinth','other'),{})
    def test_snapshot_exposes_pack_identity_for_search_badges(self):
        p=self.instance(self.root);pack_status.record(p,'modrinth','project','file')
        row=sidecar.scan_versions(self.root,{})[0]
        self.assertEqual((row['packSource'],row['packProject']),('modrinth','project'))
    def test_same_name_in_two_roots_preserved(self):
        a=self.root/'a';b=self.root/'b'
        for root in (a,b):pack_status.record(self.instance(root),'modrinth','project','file')
        matches=pack_status.installed([a,b,a],'modrinth','project')['file']
        self.assertEqual(len(matches),2);self.assertEqual({m['root'] for m in matches},{str(a),str(b)})
    def test_malformed_and_unmarked_profiles_ignored(self):
        p=self.instance(self.root);self.assertEqual(pack_status.installed([self.root],'modrinth','project'),{})
        (p/'same.json').write_text('{bad');self.assertEqual(pack_status.installed([self.root],'modrinth','project'),{})
    def test_moved_instance_retains_identity(self):
        p=self.instance(self.root/'old');pack_status.record(p,'modrinth','project','file')
        new=self.root/'new/versions';new.mkdir(parents=True);p.rename(new/'same')
        self.assertEqual(pack_status.installed([self.root/'new'],'modrinth','project')['file'][0]['root'],str(self.root/'new'))
    def test_online_install_records_selected_file_and_stages(self):
        instance=self.instance(self.root);events=[]
        with patch.object(sidecar,'_pack_context',return_value=({},net,self.root)),patch.object(net,'modrinth_version',return_value={'id':'file','project_id':'project'}),patch.object(net,'install_modrinth_version',side_effect=lambda version,dest:[dest/'pack.mrpack']),patch.object(sidecar,'import_modpack',return_value={'ok':True,'version':'same','path':str(instance)}):
            result=sidecar._install_online_pack('modrinth','project','file',events.append)
        self.assertTrue(result['ok']);self.assertEqual(result['installedVersion'],'same')
        self.assertIn('@phase|正在下载整合包索引文件',events)
        self.assertIn('file',pack_status.installed([self.root],'modrinth','project'))
    def test_offline_pack_emits_actual_phases_in_order(self):
        archive=self.root/'test.mrpack'
        with zipfile.ZipFile(archive,'w') as z:z.writestr('modrinth.index.json',json.dumps({'game':'minecraft','formatVersion':1,'name':'Pack','dependencies':{'minecraft':'1.20.1'},'files':[]}))
        events=[]
        with patch.object(modpack,'_prepare_parent',return_value='1.20.1'):
            result=modpack.install_archive(archive,self.root/'game',on_log=events.append)
        phases=[s[7:] for s in events if s.startswith('@phase|')]
        self.assertEqual(phases,['正在准备下载内容','准备整合包安装目录','解压整合包配置','写入整合包实例'])
        self.assertTrue((Path(result['dir'])/(result['vid']+'.json')).is_file())
    def test_loader_preparation_emits_only_needed_loader_stage(self):
        base=self.instance(self.root,'1.20.1');(base/'1.20.1.jar').write_bytes(b'jar')
        for loader in ('Fabric','Quilt','Forge','NeoForge'):
            events=[]
            def version(root,name):return type('V',(),{'main_class':'Main','raw':{},'loader':'Vanilla','loader_version':''})()
            with patch.object(core,'Version',side_effect=version),patch.object(loaders,'install_loader',return_value='generated'):
                modpack._prepare_parent(self.root,{'mc':'1.20.1','loader':loader,'loader_version':'1'},events.append)
            self.assertEqual(events,[f'@phase|安装 {loader} 1'])

if __name__=='__main__':unittest.main(verbosity=2)
