"""Compare v0.7 food changes with the immutable v0.6.5 source/runtime release."""
from pathlib import Path
from copy import deepcopy
import hashlib,json,xml.etree.ElementTree as ET,zipfile

ROOT=Path(__file__).resolve().parents[1]
PROMOTED=set('Reishi TurkeyTail Sanghuang Cordyceps Chaga ZombieAntFungus DryRot DevilsFingers BirdsNest Chlorophos'.split())
PREFIXES=('About/','Assemblies/','Defs/','Languages/','Textures/','Credits/','Patches/')
CHANGED={
 'About/About.xml','Assemblies/RimMushrooms.dll','Defs/ExpansionMushrooms/Mushrooms.xml',
 'Defs/PoisonMushrooms/Categories.xml','Defs/PoisonMushrooms/PoisonMushrooms.xml',
 *('Languages/'+lang+'/DefInjected/ThingDef/'+n+'.xml' for lang in ('English','Korean') for n in ('ExpansionMushrooms','PoisonMushrooms'))}
ADDED={'Languages/'+lang+'/Keyed/FoodSettings.xml' for lang in ('English','Korean')}
checks=0
def check(value,message):
 global checks
 checks+=1
 if not value:raise AssertionError(message)
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def sig(node):return (node.tag,tuple(sorted(node.attrib.items())),(node.text or '').strip(),tuple(sig(c) for c in node))
def definitions(files):
 result={}
 for name,data in files.items():
  if name.startswith('Defs/') and name.endswith('.xml'):
   for node in ET.fromstring(data):
    key=(node.tag,node.findtext('defName') or node.get('Name'))
    if key[1]:check(key not in result,'unique definition '+str(key));result[key]=node
 return result
def normalized(node):
 node=deepcopy(node);name=node.findtext('defName') or node.get('Name','')
 if node.tag=='ThingDef' and name.startswith(('RMush_Plant','RMush_Raw')):
  description=node.find('description')
  if description is not None:node.remove(description)
  if name.removeprefix('RMush_Raw') in PROMOTED:node.set('ParentName','PROMOTED_TO_FOOD')
 if name=='RMush_PoisonRawBase':
  node.set('ParentName','EDIBLE_POISON_BASE')
  node.find('statBases/Nutrition').text='NUTRITION_CHANGE'
  node.find('ingestible/preferability').text='PREFERENCE_CHANGE'
  node.find('thingCategories').attrib.clear()
 if node.tag=='ThingCategoryDef' and name=='RMush_PoisonMushrooms':node.find('parent').text='FOOD_CATEGORY_CHANGE'
 return sig(node)
def main():
 with zipfile.ZipFile(ROOT/'Releases/v0.6.5/MoreMushrooms-v0.6.5.zip') as z:
  old={n.removeprefix('MoreMushrooms/'):z.read(n) for n in z.namelist() if n.removeprefix('MoreMushrooms/').startswith(PREFIXES)}
 current={p.relative_to(ROOT).as_posix():p.read_bytes() for prefix in PREFIXES for p in (ROOT/prefix).rglob('*') if p.is_file()}
 check(set(current)-set(old)==ADDED,'only two settings translation files are added to gameplay payload')
 check(not set(old)-set(current),'no gameplay payload removed')
 for name,data in old.items():
  if name not in CHANGED:check(current[name]==data,'unchanged runtime file '+name)
 old_defs,new_defs=definitions(old),definitions(current)
 check(set(old_defs)==set(new_defs),'all definitions and abstract identities preserved')
 for key,node in old_defs.items():check(normalized(node)==normalized(new_defs[key]),'preserved definition fields '+str(key))
 before_about=ET.fromstring(old['About/About.xml']);after_about=ET.fromstring(current['About/About.xml'])
 before_about.find('modVersion').text=after_about.find('modVersion').text
 check(sig(before_about)==sig(after_about),'only About version changes')
 with zipfile.ZipFile(ROOT/'Releases/v0.6.5/MoreMushrooms-v0.6.5-source.zip') as z:
  for name in ('mushrooms','poison_mushrooms','expansion_mushrooms','wild_ecology'):
   before=json.loads(z.read('MoreMushrooms/Balance/'+name+'.json'));after=json.loads((ROOT/'Balance'/(name+'.json')).read_text(encoding='utf-8'))
   for c in (before,after):
    for k in ('version','contentVersion','status'):c.pop(k,None)
   if name=='poison_mushrooms':before['common']['nutrition']=after['common']['nutrition']=.05
   if name=='expansion_mushrooms':
    check(before['notes'][:2]+before['notes'][3:]==after['notes'][:2]+after['notes'][3:],'all unrelated expansion notes preserved')
    check(after['notes'][2]=='v0.7.0: the ten former medicinal and other fungal resources are edible cooking ingredients; medicinal gameplay and new special effects remain deferred.','expansion notes reflect the approved food promotion')
    before['notes'][2]=after['notes'][2]
    for c in (before,after):
     for m in c['mushrooms']:
      if m['id'] in PROMOTED:m['use']='FOOD_PROMOTION';m['description']='APPROVED_DESCRIPTION';m.pop('plantFood',None)
   check(before==after,'all unrelated balance and ecology values preserved '+name)
  for name in z.namelist():
   rel=name.removeprefix('MoreMushrooms/')
   if (rel.startswith('Source/') or rel=='MoreMushrooms.csproj') and rel!='Source/MushroomMealExposure.cs':
    check((ROOT/rel).read_bytes()==z.read(name),'unchanged prior production source '+rel)
  before=z.read('MoreMushrooms/Source/MushroomMealExposure.cs').decode('utf-8')
  after=(ROOT/'Source/MushroomMealExposure.cs').read_text(encoding='utf-8')
  check(before[:before.index('            var legacy = new HashSet')] .replace('\r\n','\n')==after[:after.index('            // v0.7 changes existing resources')].replace('\r\n','\n'),'meal exposure paths before legacy migration unchanged')
  check(before[before.index('            foreach (var food in DefDatabase'):].replace('\r\n','\n')==after[after.index('            foreach (var food in DefDatabase'):].replace('\r\n','\n'),'legacy migration iteration and remaining source unchanged')
 session_path=ROOT/'Tests/Runtime/v070-before.json'
 archives=0
 if session_path.is_file():
  session=json.loads(session_path.read_text(encoding='utf-8'))
  for rel,digest in session['archives'].items():check(sha(ROOT/rel)==digest,'immutable archive '+rel);archives+=1
  config=Path.home()/'AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config'
  for rel,digest in session['userConfig'].items():check(sha(config/rel)==digest,'user config preserved '+rel)
 check((ROOT/'Docs/History/README-v0.6.5.md').read_bytes()==zipfile.ZipFile(ROOT/'Releases/v0.6.5/MoreMushrooms-v0.6.5.zip').read('MoreMushrooms/README.md'),'previous README preserved exactly')
 report=dict(status='PASS',checks=checks,baselineVersion='0.6.5',runtimePayloadBefore=len(old),runtimePayloadAfter=len(current),definitionCount=len(old_defs),approvedRuntimeChanges=sorted(CHANGED),addedFiles=sorted(ADDED),priorArchivesPreserved=archives)
 (ROOT/'Docs/TestResults/v0.7.0-preservation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
 print(json.dumps(report,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
