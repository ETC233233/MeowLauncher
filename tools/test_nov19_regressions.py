"""Regression coverage for retries, independent instance settings and non-destructive loader changes."""
import hashlib, importlib.util, json, sys, tempfile, unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ml2 import net, isolation, launch_profile, core, loaders
spec = importlib.util.spec_from_file_location('sidecar_nov19', Path(__file__).resolve().parents[1]/'MeowLauncher.WinUI/Backend/sidecar.py')
sidecar = importlib.util.module_from_spec(spec); spec.loader.exec_module(sidecar)

class AssetRecovery(unittest.TestCase):
    def run_assets(self, count, failures=1, duplicate=False, permanent=False):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); index=root/'assets/indexes/test.json';index.parent.mkdir(parents=True)
            bodies={hashlib.sha1(str(i).encode()).hexdigest():str(i).encode() for i in range(count)}
            objects={str(i):{'hash':h,'size':len(body)} for i,(h,body) in enumerate(bodies.items())}
            if duplicate: objects['alias']=next(iter(objects.values()))
            index.write_text(json.dumps({'objects':objects}))
            calls={}; victim=next(iter(bodies))
            def download(url,dest,on_progress=None,**kwargs):
                h=dest.name; calls[h]=calls.get(h,0)+1
                self.assertEqual(kwargs.get('sha1'),h);self.assertEqual(kwargs.get('size'),len(bodies[h]))
                if h==victim and (permanent or calls[h]<=failures): raise OSError('temporary reset')
                dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(bodies[h])
            def batch(jobs,on_bytes,on_file,**kwargs):
                failed=0
                for job in jobs:
                    try: download(job['url'],job['dest'],sha1=job['sha1'],size=job['size']);on_file(str(job['dest']))
                    except OSError:failed+=1
                return failed
            with patch.object(net,'_download',download),patch.object(net,'download_jobs_multi',batch),patch.object(net.time,'sleep'):
                result=net.ensure_assets(root,'test',workers=2)
            self.assertEqual(result,(count-1,1) if permanent else (count,0))
            self.assertTrue(all(calls[h]==1 for h in bodies if h!=victim))
            return calls[victim]
    def test_small_transient(self):self.assertEqual(self.run_assets(3),2)
    def test_batch_transient(self):self.assertEqual(self.run_assets(120),2)
    def test_persistent_failure_reported(self):self.assertEqual(self.run_assets(3,permanent=True),4)
    def test_duplicate_hash_downloaded_once(self):self.assertEqual(self.run_assets(3,failures=0,duplicate=True),1)

class InstanceData(unittest.TestCase):
    def setUp(self):self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
    def tearDown(self):self.temp.cleanup();isolation.set_resolver(None)
    def test_root_scoped_memory(self):
        a=self.root/'a';b=self.root/'b'
        st={'perInstance':{'same':{'mem':2048,'useGlobal':False},launch_profile.instance_key(a,'same'):{'mem':8192,'useGlobal':False}}}
        self.assertEqual(launch_profile.resolve_launch_profile(st,'same',a).memory_mb,8192)
        self.assertEqual(launch_profile.resolve_launch_profile(st,'same',b).memory_mb,2048)
    def seed(self):
        p=self.root/'saves/world/level.dat';p.parent.mkdir(parents=True);p.write_bytes(b'world')
        (self.root/'options.txt').write_text('settings')
    def test_isolation_roundtrip_keeps_settings(self):
        self.seed();instance=self.root/'versions/demo'
        isolation.migrate_content(self.root,'demo',False,True)
        self.assertEqual((instance/'saves/world/level.dat').read_bytes(),b'world')
        self.assertTrue((instance/'options.txt').exists())
        isolation.migrate_content(self.root,'demo',True,False)
        self.assertTrue((self.root/'saves/world/level.dat').exists());self.assertTrue((instance/'options.txt').exists())
    def test_collision_no_changes(self):
        self.seed();dest=self.root/'versions/demo/saves/world/level.dat';dest.parent.mkdir(parents=True);dest.write_bytes(b'other')
        with self.assertRaises(FileExistsError):isolation.migrate_content(self.root,'demo',False,True)
        self.assertEqual(dest.read_bytes(),b'other');self.assertEqual((self.root/'saves/world/level.dat').read_bytes(),b'world')
    def test_move_failure_rolls_back(self):
        self.seed();import shutil
        original=shutil.move;calls=0
        def fail_second(src,dst):
            nonlocal calls
            calls+=1
            if calls==2:raise PermissionError('in use')
            return original(src,dst)
        with patch('shutil.move',fail_second):
            with self.assertRaises(PermissionError):isolation.migrate_content(self.root,'demo',False,True)
        self.assertTrue((self.root/'saves/world/level.dat').exists());self.assertTrue((self.root/'options.txt').exists())
        self.assertFalse(isolation.probe_isolated(self.root, 'demo'))
    def loader_change(self, fail=False):
        game=self.root/'game';target=game/'versions/custom';target.mkdir(parents=True)
        original={'id':'custom','clientVersion':'1.20.1','mainClass':'old'}
        (target/'custom.json').write_text(json.dumps(original));(target/'custom.jar').write_bytes(b'client')
        (target/'saves').mkdir();(target/'saves/world.dat').write_bytes(b'world')
        (target/'mods').mkdir();(target/'mods/existing.jar').write_bytes(b'mod')
        (target/'options.txt').write_bytes(b'options')
        def loader(root,base,kind,version,**kw):
            if fail:raise OSError('network reset')
            d=root/'versions/generated';d.mkdir(parents=True)
            (d/'generated.json').write_text(json.dumps({'id':'generated','inheritsFrom':base,'mainClass':'new','libraries':[{'name':'net.fabricmc:fabric-loader:0.16'}]}))
            return 'generated'
        patches=[patch.object(sidecar,'load_settings',return_value={'mcDir':str(game)}),patch.object(core,'user_dir',return_value=self.root/'user'),patch.object(net,'mojang_versions',return_value=[{'id':'1.20.1','url':'fake'}]),patch.object(net,'mojang_version_json',return_value={'id':'1.20.1','mainClass':'vanilla'}),patch.object(loaders,'install_loader',loader)]
        from contextlib import ExitStack
        with ExitStack() as stack:
            for p in patches:stack.enter_context(p)
            if fail:
                with self.assertRaises(OSError):sidecar.install_existing_loader('custom','fabric','0.16')
            else:self.assertTrue(sidecar.install_existing_loader('custom','fabric','0.16')['ok'])
        profile=json.loads((target/'custom.json').read_text())
        self.assertEqual(profile['id'],'custom');self.assertEqual(profile['mainClass'],'old' if fail else 'new')
        self.assertNotIn('inheritsFrom',profile)
        for file,body in [('saves/world.dat',b'world'),('mods/existing.jar',b'mod'),('options.txt',b'options')]:self.assertEqual((target/file).read_bytes(),body)
    def test_loader_success_keeps_data(self):self.loader_change()
    def test_loader_failure_keeps_profile(self):self.loader_change(True)

class InstallSafety(unittest.TestCase):
    def test_new_loader_never_overwrites_generated_name(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);base=root/'versions/1.20.1';base.mkdir(parents=True)
            (base/'1.20.1.json').write_text(json.dumps({'id':'1.20.1','mainClass':'vanilla'}))
            (base/'1.20.1.jar').write_bytes(b'client')
            existing=root/'versions/generated';existing.mkdir()
            (existing/'generated.json').write_bytes(b'old profile')
            (existing/'saves').mkdir();(existing/'saves/world.dat').write_bytes(b'world')
            def install(stage,*args,**kwargs):
                generated=stage/'versions/generated';generated.mkdir(parents=True)
                (generated/'generated.json').write_text(json.dumps({'id':'generated','inheritsFrom':'1.20.1','mainClass':'fabric'}))
                return 'generated'
            with patch.object(loaders,'install_loader',install):
                result=sidecar._install_new_loader(root,'1.20.1','fabric','0.16','my-instance',lambda s:None)
            self.assertEqual(result,'my-instance')
            self.assertEqual((existing/'generated.json').read_bytes(),b'old profile')
            self.assertEqual((existing/'saves/world.dat').read_bytes(),b'world')
            profile=json.loads((root/'versions/my-instance/my-instance.json').read_text())
            self.assertEqual(profile['id'],'my-instance');self.assertNotIn('inheritsFrom',profile)
            self.assertTrue((root/'.meow-archive/loader-install/MOVES.csv').exists())
    def test_empty_default_uses_next_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);empty=root/'empty';empty.mkdir();a=root/'a';b=root/'b'
            for game in [a,b]:
                instance=game/'versions/same';instance.mkdir(parents=True)
                (instance/'same.json').write_text(json.dumps({'id':'same','clientVersion':'1.20.1'}))
            settings={'mcDir':str(empty),'mcDirs':[str(empty),str(a),str(b)]}
            with patch.object(sidecar,'load_settings',return_value=settings),patch.object(sidecar,'memory_info',return_value={}),patch.object(sidecar,'online_cached_only',return_value={}):
                result=sidecar.make_snapshot()
            self.assertEqual(len(result['versions']),2)
            self.assertEqual(result['current']['root'],str(a))
            self.assertEqual(len({v['root'] for v in result['versions']}),2)
    def test_host_directory_list_is_used(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);empty=root/'empty';empty.mkdir();other=root/'other'
            instance=other/'versions/demo';instance.mkdir(parents=True)
            (instance/'demo.json').write_text(json.dumps({'id':'demo','clientVersion':'1.20.1'}))
            settings={'mcDir':str(empty),'mcDirs':[str(empty)]}
            with patch.object(sidecar,'load_settings',return_value=settings),patch.object(sidecar,'memory_info',return_value={}),patch.object(sidecar,'online_cached_only',return_value={}),patch.dict(sidecar.os.environ,{'MEOW_MC_DIRS':json.dumps([str(empty),str(other)])}):
                result=sidecar.make_snapshot()
            self.assertEqual(result['current']['root'],str(other))

    def test_name_check_does_not_submit_rename(self):
        class Response:
            status=200
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self):return b'{"status":"AVAILABLE"}'
        with patch.object(sidecar,'_mc_access_token',return_value='synthetic'),patch('urllib.request.urlopen',return_value=Response()) as request:
            result=sidecar.mc_rename('New_Name',check_only=True)
        self.assertTrue(result['ok']);self.assertEqual(result['message'],'该昵称可以使用')
        self.assertEqual(request.call_count,1);self.assertEqual(request.call_args.args[0].method,'GET')
    def test_name_taken_does_not_submit_rename(self):
        class Response:
            status=200
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self):return b'{"status":"DUPLICATE"}'
        with patch.object(sidecar,'_mc_access_token',return_value='synthetic'),patch('urllib.request.urlopen',return_value=Response()) as request:
            result=sidecar.mc_rename('Taken_Name')
        self.assertFalse(result['ok']);self.assertEqual(result['message'],'该昵称已被占用')
        self.assertEqual(request.call_count,1);self.assertEqual(request.call_args.args[0].method,'GET')

if __name__=='__main__':unittest.main(verbosity=2)
