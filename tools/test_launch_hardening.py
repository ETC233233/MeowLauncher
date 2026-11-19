"""Launch regressions using synthetic accounts/files only; never starts Minecraft."""
import base64
import hashlib
import importlib.util
import json
import subprocess
import sys
import tempfile
import time
import unittest
import zipfile
from contextlib import ExitStack
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from ml2 import auth, core, health, integrity, launch_profile, net, session
spec = importlib.util.spec_from_file_location('sidecar_launch_test', ROOT/'MeowLauncher.WinUI/Backend/sidecar.py')
sidecar = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sidecar)
UUID = '12345678123456781234567812345678'


class Sessions(unittest.TestCase):
    def setUp(self):
        self.stack = ExitStack(); self.addCleanup(self.stack.close)
        self.owned = self.stack.enter_context(patch.object(auth, 'entitlements', return_value=['game_minecraft']))
        self.profile = self.stack.enter_context(patch.object(auth, 'minecraft_profile', return_value={'id':UUID, 'name':'ServerName'}))
        self.refresh = self.stack.enter_context(patch.object(auth, 'refresh_msa', return_value={'access_token':'msa-test', 'refresh_token':'rotated-test'}))
        self.stack.enter_context(patch.object(auth, 'xbox_live', return_value={'token':'xbl-test'}))
        self.stack.enter_context(patch.object(auth, 'xsts', return_value={'token':'xsts-test', 'uhs':'hash-test', 'xuid':'12345'}))
        self.stack.enter_context(patch.object(auth, 'minecraft_login', return_value={'access_token':'fresh-test', 'expires_in':3600}))
        self.account = dict(type='microsoft', name='Cached', uuid=UUID, access_token='cached-test', refresh_token='refresh-test')
    def test_valid_session_uses_authoritative_profile(self):
        out = session.validate_account(self.account, 'client-test')
        self.assertEqual((out['name'],out['uuid'],out['access_token']), ('ServerName',UUID,'cached-test'))
        self.refresh.assert_not_called(); self.assertEqual(self.account['name'], 'Cached')
    def test_expired_token_refreshes_full_chain(self):
        self.account['expiresAt'] = time.time()-10
        out = session.validate_account(self.account, 'client-test')
        self.refresh.assert_called_once_with('refresh-test','client-test')
        self.assertEqual((out['access_token'],out['refresh_token'],out['xuid']), ('fresh-test','rotated-test','12345'))
        self.assertGreater(out['expiresAt'],time.time()+3500)
    def test_401_refreshes(self):
        self.owned.side_effect = [auth.AuthError('expired',401), ['game_minecraft']]
        self.assertEqual(session.validate_account(self.account,'client-test')['access_token'], 'fresh-test')
    def test_network_failure_preserves_credentials(self):
        self.owned.side_effect = auth.AuthError('fake network failure')
        with self.assertRaises(session.SessionError) as ctx: session.validate_account(self.account,'client-test')
        self.assertEqual(ctx.exception.code,'AUTH-NETWORK');self.assertFalse(ctx.exception.need_login)
        self.refresh.assert_not_called();self.assertEqual(self.account['refresh_token'],'refresh-test')
    def test_refresh_rejection_requests_login(self):
        self.account['expiresAt'] = 1;self.refresh.side_effect = auth.AuthError('invalid_grant',400)
        with self.assertRaises(session.SessionError) as ctx: session.validate_account(self.account,'client-test')
        self.assertEqual(ctx.exception.code,'AUTH-EXPIRED');self.assertTrue(ctx.exception.need_login)
    def test_no_refresh_requests_login(self):
        self.account.update(expiresAt=1,refresh_token='')
        with self.assertRaises(session.SessionError) as ctx: session.validate_account(self.account,'client-test')
        self.assertTrue(ctx.exception.need_login)
    def test_malformed_expiry_is_refreshed(self):
        self.account['expiresAt']='broken'
        self.assertEqual(session.validate_account(self.account,'client-test')['access_token'],'fresh-test')
    def test_bedrock_entitlement_does_not_grant_java(self):
        self.owned.return_value=['game_minecraft_bedrock']
        with self.assertRaises(session.SessionError) as ctx: session.validate_account(self.account,'client-test')
        self.assertEqual(ctx.exception.code,'AUTH-NO-OWNERSHIP')
    def test_missing_profile_is_distinct(self):
        self.profile.side_effect=auth.AuthError('not found',404)
        with self.assertRaises(session.SessionError) as ctx: session.validate_account(self.account,'client-test')
        self.assertEqual(ctx.exception.code,'AUTH-PROFILE')
    def test_invalid_uuid_rejected(self):
        self.profile.return_value={'id':'bad','name':'Player'}
        with self.assertRaises(session.SessionError) as ctx: session.validate_account(self.account,'client-test')
        self.assertEqual(ctx.exception.code,'AUTH-PROFILE')
    def test_offline_makes_no_requests(self):
        account={'type':'offline','name':'Local'}
        self.assertEqual(session.validate_account(account,'client-test'),account)
        self.owned.assert_not_called();self.refresh.assert_not_called()
    def test_xuid_claim_after_server_validation(self):
        payload=base64.urlsafe_b64encode(json.dumps({'xuid':'98765'}).encode()).decode().rstrip('=')
        self.account['access_token']='header.'+payload+'.signature'
        self.assertEqual(session.validate_account(self.account,'client-test')['xuid'],'98765')
    def test_launch_cannot_bypass_failed_auth(self):
        with patch.object(sidecar,'_prune_running',return_value=[]), patch.object(sidecar,'load_settings',return_value={}), patch.object(sidecar,'active_account',return_value=self.account), patch.object(session,'validate_account',side_effect=session.SessionError('test','AUTH-EXPIRED',True)), patch.object(core.Launcher,'start') as start, patch.object(sidecar,'save_settings') as save:
            result=sidecar.launch_version('test')
        self.assertEqual(result['errorCode'],'AUTH-EXPIRED');start.assert_not_called();save.assert_not_called()
    def test_pkce_ownership_list_and_network_error(self):
        with patch.object(auth,'exchange_code_pkce',return_value={'access_token':'msa-test'}):
            self.assertTrue(auth.login_with_pkce_code('code','verifier','client')['owns_game'])
            self.owned.side_effect=auth.AuthError('network')
            with self.assertRaises(auth.AuthError):auth.login_with_pkce_code('code','verifier','client')
    def test_initial_login_persists_validated_account(self):
        settings={};events=[]
        with patch.object(auth,'_post_form',return_value={'access_token':'msa-test','refresh_token':'refresh-test'}),patch.object(sidecar,'load_settings',return_value=settings),patch.object(sidecar,'_ms_client_id'),patch.object(sidecar,'save_settings') as save,patch.object(sidecar,'_cache_profile_textures'):
            result=sidecar.ms_poll('device-test',events.append)
        self.assertTrue(result['ok']);self.assertEqual(events,['authorized']);save.assert_called_once()
        self.assertEqual(settings['accounts'][0]['name'],'ServerName');self.assertTrue(settings['accounts'][0]['ownsGame'])


class Files(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.metadata={'id':'test','mainClass':'example.Main','libraries':[], 'arguments':{'game':['--username','${auth_player_name}']}}
    def write(self, relative, body):
        path=self.root/relative;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(body);return path
    def version(self):
        self.write('versions/test/test.json',json.dumps(self.metadata).encode())
        self.write('versions/test/test.jar',b'client')
        return core.Version(self.root,'test')
    def library(self):
        art={'path':'org/test/1/test.jar','url':'https://test.invalid/lib','sha1':hashlib.sha1(b'library').hexdigest(),'size':7}
        native={'path':'org/test/1/natives.jar','url':'https://test.invalid/native'}
        self.metadata['libraries']=[{'name':'org:test:1','downloads':{'artifact':art,'classifiers':{'natives-windows':native}},'natives':{'windows':'natives-windows'},'extract':{'exclude':['skip/']}}]
        self.write('libraries/'+art['path'],b'library')
        path=self.root/'libraries'/native['path'];path.parent.mkdir(parents=True,exist_ok=True)
        with zipfile.ZipFile(path,'w') as z:
            z.writestr('META-INF/test','meta');z.writestr('skip/test.dll','skip');z.writestr('sub/test.dll',b'dll')
        return art,native,path
    def index(self, flags=None):
        body=b'asset';h=hashlib.sha1(body).hexdigest()
        index=json.dumps(dict(objects={'folder/sound.ogg':{'hash':h,'size':len(body)}},**(flags or {}))).encode()
        self.metadata['assetIndex']={'id':'test-index','url':'https://test.invalid/index','sha1':hashlib.sha1(index).hexdigest(),'size':len(index)}
        self.write('assets/indexes/test-index.json',index);self.write('assets/objects/'+h[:2]+'/'+h,body)
        return h,index
    def test_both_artifact_and_native_checked_but_only_artifact_classpath(self):
        art,native,_=self.library();ver=self.version()
        self.assertEqual(len(list(ver.selected_libraries())),2)
        self.assertEqual(ver.libraries(),[self.root/'libraries'/art['path']])
        self.assertEqual(health.version_gaps(ver)['gaps'],[])
    def test_same_size_corruption_detected(self):
        art,_,_=self.library();ver=self.version();self.write('libraries/'+art['path'],b'damaged')
        self.assertEqual([x['kind'] for x in health.version_gaps(ver)['gaps']],['library'])
    def test_corrupt_client_detected(self):
        self.metadata['downloads']={'client':{'sha1':hashlib.sha1(b'client').hexdigest(),'size':6}}
        ver=self.version();self.write('versions/test/test.jar',b'broken')
        self.assertEqual(health.version_gaps(ver)['gaps'][0]['kind'],'client')
    def test_malformed_index_is_not_clean(self):
        self.metadata['assets']='test-index';ver=self.version();self.write('assets/indexes/test-index.json',b'{bad')
        self.assertEqual(health.version_gaps(ver)['gaps'][0]['kind'],'asset-index')
    def test_bad_asset_hash_cannot_be_silently_skipped_by_install(self):
        self.write('assets/indexes/bad.json',json.dumps({'objects':{'bad':{'hash':'broken'}}}).encode())
        with self.assertRaises(RuntimeError):net.assets_missing(self.root,'bad')
    def test_shared_asset_hash_has_one_repair(self):
        h,_=self.index();path=self.root/'assets/indexes/test-index.json';data=json.loads(path.read_text());data['objects']['alias']=data['objects']['folder/sound.ogg'];path.write_text(json.dumps(data))
        self.metadata['assetIndex'].pop('sha1');self.metadata['assetIndex'].pop('size')
        self.write('assets/objects/'+h[:2]+'/'+h,b'bad')
        self.assertEqual(len(health.version_gaps(self.version())['gaps']),1)
    def test_native_excludes_and_paths(self):
        self.library();dest=self.root/'out';self.version().extract_natives(dest)
        self.assertEqual((dest/'sub/test.dll').read_bytes(),b'dll');self.assertFalse((dest/'skip').exists());self.assertFalse((dest/'META-INF').exists())
    def test_native_traversal_stops(self):
        _,_,path=self.library()
        with zipfile.ZipFile(path,'a') as z:z.writestr('../escape.dll',b'bad')
        with self.assertRaises(RuntimeError):self.version().extract_natives(self.root/'out')
        self.assertFalse((self.root/'escape.dll').exists())
    def test_corrupt_native_stops_command_creation(self):
        _,_,path=self.library();path.write_bytes(b'not zip')
        with self.assertRaises(RuntimeError):self.version().build_command(core.LaunchOptions('test',java=Path('java')))
    def test_virtual_and_resources_use_isolated_game_directory(self):
        self.index({'virtual':True,'map_to_resources':True});ver=self.version();game=self.root/'versions/test'
        self.assertEqual(ver.prepare_assets(game),self.root/'assets/virtual/test-index')
        self.assertEqual((game/'resources/folder/sound.ogg').read_bytes(),b'asset')
        self.assertFalse((self.root/'resources').exists())
    def test_normal_assets_do_not_copy(self):
        self.index();self.assertEqual(self.version().prepare_assets(self.root),self.root/'assets')
        self.assertFalse((self.root/'resources').exists())
    def test_asset_mapping_rejects_escape(self):
        h,_=self.index({'virtual':True});self.write('assets/indexes/test-index.json',json.dumps({'virtual':True,'objects':{'../escape':{'hash':h,'size':5}}}).encode())
        with self.assertRaises(ValueError):self.version().prepare_assets(self.root)
    def test_index_repair_rechecks_and_downloads_newly_discovered_assets(self):
        h,index=self.index();self.write('assets/indexes/test-index.json',b'bad');self.write('assets/objects/'+h[:2]+'/'+h,b'bad');self.version();calls=[]
        def download(url,dest,*args,**kw):
            body=index if url.endswith('/index') else b'asset'
            self.assertEqual(kw['sha1'],hashlib.sha1(body).hexdigest());self.assertEqual(kw['size'],len(body));dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(body);calls.append(url)
        with patch.object(sidecar,'load_settings',return_value={}),patch.object(sidecar,'game_dir',return_value=self.root),patch.object(net,'_download',download):result=sidecar.repair_version('test')
        self.assertTrue(result['ok'],result);self.assertEqual(len(calls),2);self.assertEqual(health.version_gaps(core.Version(self.root,'test'))['gaps'],[])
    def test_download_success_without_correct_content_cannot_pass_repair(self):
        art,_,_=self.library();self.write('libraries/'+art['path'],b'bad');self.version()
        with patch.object(sidecar,'load_settings',return_value={}),patch.object(sidecar,'game_dir',return_value=self.root),patch.object(net,'_download',return_value=None):result=sidecar.repair_version('test')
        self.assertFalse(result['ok']);self.assertEqual(result['errorCode'],'FILE-INTEGRITY')
    def test_install_passes_all_hashes_to_downloader(self):
        art,_,_=self.library();_,index=self.index();self.metadata['downloads']={'client':{'url':'https://test.invalid/client','sha1':hashlib.sha1(b'client').hexdigest(),'size':6}}
        self.write('libraries/'+art['path'],b'bad');self.write('assets/indexes/test-index.json',b'bad');calls=[]
        def download(url,dest,*args,**kw):
            body=index if url.endswith('/index') else b'client';dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(body);calls.append(kw)
        def batch(jobs,on_bytes,on_file,**kw):
            for job in jobs:
                self.assertEqual(job['sha1'],art['sha1']);self.assertEqual(job['size'],7);job['dest'].write_bytes(b'library');on_file(str(job['dest']))
            return 0
        with patch.object(net,'_download',download),patch.object(net,'download_jobs_multi',batch),patch.object(net,'ensure_version_gamedir'):
            net.install_vanilla(self.root,self.metadata)
        self.assertEqual({x['sha1'] for x in calls},{hashlib.sha1(index).hexdigest(),hashlib.sha1(b'client').hexdigest()})
    def test_logging_repairs_bad_cached_file_and_uses_metadata_argument(self):
        body=b'<config/>';self.metadata['logging']={'client':{'argument':'-Dcustom.config=${path}','file':{'id':'config.xml','url':'https://test.invalid/log','size':len(body),'sha1':hashlib.sha1(body).hexdigest()}}};self.write('assets/log_configs/config.xml',b'invalid!!')
        def download(url,dest,**kw):self.assertEqual(kw['sha1'],hashlib.sha1(body).hexdigest());dest.write_bytes(body)
        with patch.object(net,'_download',download):cmd,_=self.version().build_command(core.LaunchOptions('test',java=Path('java')))
        self.assertTrue(any(x.startswith('-Dcustom.config=') for x in cmd));self.assertEqual((self.root/'assets/log_configs/config.xml').read_bytes(),body)
    def test_missing_parent_and_cyclic_inheritance_stop(self):
        self.metadata={'id':'test','inheritsFrom':'missing'}
        with self.assertRaises(FileNotFoundError):self.version()
        self.metadata={'id':'test','inheritsFrom':'test'}
        with self.assertRaises(ValueError):self.version()
    def test_incomplete_verification_stops_launch(self):
        self.version();account={'type':'offline','name':'Local'}
        with patch.object(sidecar,'_prune_running',return_value=[]),patch.object(sidecar,'load_settings',return_value={}),patch.object(sidecar,'active_account',return_value=account),patch.object(sidecar,'game_dir',return_value=self.root),patch.object(sidecar,'_version_file_gaps',side_effect=OSError('read failed')),patch.object(core.Launcher,'start') as start:
            result=sidecar.launch_version('test')
        self.assertEqual(result['errorCode'],'FILE-VERIFY');start.assert_not_called()
    def test_java_mismatch_stops_before_process(self):
        self.metadata['javaVersion']={'majorVersion':21};self.version();java=self.write('java/bin/java.exe',b'fake');account={'type':'offline','name':'Local'}
        with patch.object(sidecar,'_prune_running',return_value=[]),patch.object(sidecar,'load_settings',return_value={}),patch.object(sidecar,'active_account',return_value=account),patch.object(sidecar,'game_dir',return_value=self.root),patch.object(sidecar,'_instance_tuning',return_value=SimpleNamespace(memory_mb=4096,java_path=str(java),jvm='')),patch.object(core,'java_details',return_value={'major':17,'arch':'x86_64'}),patch.object(core.Launcher,'start') as start:
            result=sidecar.launch_version('test')
        self.assertEqual(result['errorCode'],'JAVA-VERSION');start.assert_not_called()
    def test_path_guard_rejects_maven_escape(self):
        self.metadata['libraries']=[{'name':'test:../../bad:1'}]
        with self.assertRaises(ValueError):list(self.version().selected_libraries())
    def test_real_launch_entry_passes_refreshed_identity_to_launcher(self):
        self.metadata['javaVersion']={'majorVersion':21};self.version();java=self.write('java/bin/java.exe',b'fake')
        old={'type':'microsoft','name':'Cached','uuid':UUID,'access_token':'cached-test'}
        fresh=dict(old,name='ServerName',access_token='fresh-test',msClientId='client-test',xuid='12345',expiresAt=123456)
        settings={'accounts':[old]};options=[]
        def create(ver,opt):
            options.append(opt)
            return SimpleNamespace(start=lambda:SimpleNamespace(pid=0),log_path='')
        with ExitStack() as stack:
            for obj,key,value in [(sidecar,'_prune_running',[]),(sidecar,'load_settings',settings),(sidecar,'active_account',old),(sidecar,'game_dir',self.root),(session,'validate_account',fresh),(sidecar,'_instance_tuning',SimpleNamespace(memory_mb=4096,java_path=str(java),jvm='',jvm_auto=False)),(core,'java_details',{'major':21,'arch':core.system_arch()}),(sidecar,'settings_path',self.root/'settings.json')]:
                stack.enter_context(patch.object(obj,key,return_value=value))
            stack.enter_context(patch.object(sidecar,'save_settings'))
            stack.enter_context(patch.object(sidecar,'_set_priority'))
            stack.enter_context(patch.object(core.Launcher,'start',side_effect=AssertionError('real launch forbidden')))
            stack.enter_context(patch.object(core,'Launcher',side_effect=create))
            result=sidecar.launch_version('test')
        self.assertTrue(result['ok'],result);opt=options[0]
        self.assertEqual((opt.player,opt.uuid,opt.access_token,opt.client_id,opt.xuid),('ServerName',UUID,'fresh-test','client-test','12345'))
        self.assertEqual(settings['accounts'][0],fresh)
        self.assertEqual(json.loads((self.root/'settings.json').read_text())['accounts'][0],fresh)


class RulesAndArguments(unittest.TestCase):
    def test_real_os_regex_and_arch(self):
        with patch.object(core.platform,'system',return_value='Windows'),patch.object(core.platform,'version',return_value='10.0.99999'),patch.object(core.platform,'machine',return_value='AMD64'):
            self.assertTrue(core.rule_allows([{'action':'allow','os':{'name':'windows','version':r'^10\.0\.99999$','arch':'x86_64'}}]))
            self.assertFalse(core.rule_allows([{'action':'allow','os':{'arch':'aarch64'}}]))
            self.assertFalse(core.rule_allows([{'action':'allow'},{'action':'disallow','os':{'name':'windows'}}]))
    def test_feature_conditions(self):
        rules=[{'action':'allow','features':{'has_custom_resolution':True}}]
        self.assertFalse(core.rule_allows(rules,{}));self.assertTrue(core.rule_allows(rules,{'has_custom_resolution':True}))
    def test_windows_quoting_roundtrip(self):
        samples=[['-Dpath=C:\\Program Files\\Java','simple',''],['含 空格','a"b','c:\\dir\\', 'x\\"y'],['a','b\\\\','"quoted"']]
        for args in samples:self.assertEqual(core.split_arguments(subprocess.list2cmdline(args)),args)
        with self.assertRaises(ValueError):core.split_arguments('"unclosed')
    def test_command_substitutes_identity_resolution_and_quoted_paths(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);d=root/'versions/test';d.mkdir(parents=True);(d/'test.jar').write_bytes(b'client')
            metadata={'id':'test','mainClass':'example.Main','libraries':[], 'arguments':{'game':['--uuid','${auth_uuid}','--clientId','${clientid}','--xuid','${auth_xuid}','--properties','${user_properties}',{'rules':[{'action':'allow','features':{'has_custom_resolution':True}}],'value':['--width','${resolution_width}','--height','${resolution_height}']}]}}
            (d/'test.json').write_text(json.dumps(metadata));ver=core.Version(root,'test')
            options=core.LaunchOptions('test',java=Path('java'),uuid=UUID,client_id='client-test',xuid='12345',width=900,height=600,extra_jvm='"-Dpath=C:\\Program Files\\Java"',user_properties={'test':[]})
            cmd,_=ver.build_command(options)
            self.assertIn('-Dpath=C:\\Program Files\\Java',cmd);self.assertIn('client-test',cmd);self.assertIn('12345',cmd);self.assertIn('{"test":[]}',cmd)
            self.assertEqual(cmd.count('--width'),1);self.assertEqual(cmd[cmd.index('--width')+1],'900')
            ver.data['arguments']['game']=['${unknown}']
            with self.assertRaisesRegex(ValueError,'unknown'):ver.build_command(options)
    def test_legacy_argument_quoting(self):
        self.assertEqual(core.split_arguments('--name "玩家 名" --path "C:\\Game Files"'),['--name','玩家 名','--path','C:\\Game Files'])
    def test_game_arguments_and_wrapper_preserve_quoted_values(self):
        extra=dict(sidecar._EXTRA_DEFAULTS) if hasattr(sidecar,'_EXTRA_DEFAULTS') else {'width':0,'height':0,'fullscreen':False,'server':'','env':''}
        extra.update(gameArgs='--text "hello world" --text "second value"',wrapper='"C:\\Program Files\\Wrapper.exe" --wait')
        options=sidecar._extra_to_options(extra,'1.20.1')
        self.assertEqual(options['extra_game'],['--text','hello world','--text','second value'])
        self.assertEqual(options['wrapper'],['C:\\Program Files\\Wrapper.exe','--wait'])


class Java(unittest.TestCase):
    def test_metadata_priority_and_legacy_defaults(self):
        self.assertEqual(launch_profile.recommended_java_major({'javaVersion':{'majorVersion':25}},'1.12.2'),25)
        for version,expected in [('1.7.10',8),('1.16.5',8),('1.17.1',16),('1.20.4',17),('1.20.5',21),('b1.7.3',8)]:
            self.assertEqual(launch_profile.recommended_java_major({},version),expected)
        self.assertEqual(launch_profile.recommended_java_major({},'unknown'),0)
    def test_release_file_version_and_arch(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'bin').mkdir();(root/'release').write_text('JAVA_VERSION="1.8.0_452"\nOS_ARCH="amd64"')
            self.assertEqual(core.java_details(root/'bin/javaw.exe'),{'major':8,'arch':'x86_64'})
    def test_runtime_probe_uses_console_sibling(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'java.exe').write_bytes(b'fake')
            with patch.object(core.subprocess,'run',return_value=SimpleNamespace(returncode=0,stdout='',stderr='java.version = 21.0.1\nos.arch = amd64')) as run:
                self.assertEqual(core.java_details(root/'javaw.exe'),{'major':21,'arch':'x86_64'})
            self.assertEqual(run.call_args.args[0][0],str(root/'java.exe'))


if __name__ == '__main__':unittest.main(verbosity=2)
