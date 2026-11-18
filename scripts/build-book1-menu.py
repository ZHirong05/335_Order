from pathlib import Path
import json, hashlib, re, argparse, zipfile, io
import xml.etree.ElementTree as ET
from decimal import Decimal

parser = argparse.ArgumentParser(description="Build the reviewed Book1 restaurant catalog (standard library only).")
parser.add_argument('--workbook', type=Path, default=Path(__file__).resolve().parents[1] / 'Book1.xlsx')
parser.add_argument('--version', default='book1-2026-11-v5', help='Use a NEW version after editing an already imported workbook')
args = parser.parse_args()
workbook_bytes = args.workbook.read_bytes()
ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
rows = []
with zipfile.ZipFile(io.BytesIO(workbook_bytes)) as archive:
    strings = []
    if 'xl/sharedStrings.xml' in archive.namelist():
        strings = [''.join(si.itertext()) for si in ET.fromstring(archive.read('xl/sharedStrings.xml')).findall('s:si', ns)]
    for sheet in ('xl/worksheets/sheet1.xml', 'xl/worksheets/sheet2.xml'):
        for row in ET.fromstring(archive.read(sheet)).findall('.//s:sheetData/s:row', ns):
            cells = {}
            for cell in row:
                value = cell.find('s:v', ns)
                inline = cell.find('s:is', ns)
                text = value.text if value is not None else ''.join(inline.itertext()) if inline is not None else ''
                if cell.attrib.get('t') == 's': text = strings[int(text)]
                if text: cells[cell.attrib['r']] = text
            if cells: rows.append({'sheet': sheet, 'row': row.attrib['r'], 'cells': cells})

food = {int(r['row']): r['cells'] for r in rows if r['sheet'].endswith('sheet1.xml')}
drinks = {int(r['row']): r['cells'] for r in rows if r['sheet'].endswith('sheet2.xml')}
categories = []
checks = []
def category(name, beverage=False):
    result = dict(Name=name, IsBeverage=beverage, Items=[])
    categories.append(result)
    return result
def group(name, options):
    return dict(Name=name, MaxSelections=len(options) if name=='配料' else 2 if name=='面类' else 1, Options=[dict(Name=n, AdditionalPrice=float(p)) for n,p in options])
def add(cat, name, price, sources, groups=None):
    item = dict(Name=name, Price=float(price), SourceRows=sources, Groups=groups or [])
    cat['Items'].append(item)
    return item
def price(row): return Decimal(food[row][f'L{row}'])
def check(item, source, expected, *options):
    checks.append(dict(Item=item['Name'], Source=source, Price=float(expected), Options=list(options)))

noodles = category('粉面类')
curry = add(noodles, '咖喱面', price(8), [f'食品!{r}' for r in range(8,20)], [
    group('面类', [('面',0),('米粉',0),('河粉',0),('生面',price(9)-price(8))]),
    group('配料', [(n,price(r)-price(8)) for n,r in [('鸡肉',8),('叉烧',10),('花肉',12),('虾水饺（2粒）',14),('虾',16),('烧肉',18)]])])
for meat,row in [('鸡肉',8),('叉烧',10),('花肉',12),('虾水饺（2粒）',14),('虾',16),('烧肉',18)]:
    check(curry, f'食品!L{row}', price(row), '面', meat)
    check(curry, f'食品!L{row+1}', price(row+1), '生面', meat)
prawn = add(noodles, '虾汤面', price(21), [f'食品!{r}' for r in range(21,31)], [
    group('面类', [('面',0),('米粉',0),('河粉',0),('生面',price(22)-price(21))]),
    group('配料', [(n,price(r)-price(21)) for n,r in [('虾',21),('叉烧',23),('花肉',25),('水饺（2粒）',27),('烧肉',29)]])])
for meat,row in [('虾',21),('叉烧',23),('花肉',25),('水饺（2粒）',27),('烧肉',29)]:
    check(prawn, f'食品!L{row}', price(row), '面', meat)
    check(prawn, f'食品!L{row+1}', price(row+1), '生面', meat)
plain = add(noodles,'叉烧云吞面',price(32),['食品!32']);check(plain,'食品!L32',price(32))
for name,row in [('卤花肉面',33),('冬菇凤爪面（3份）',35),('虾水饺面',38),('客家面',40)]:
    gs=[group('面类',[('面',0),('米粉',0),('河粉',0),('生面',price(row+1)-price(row))])]
    if row in (33,35,38): gs.append(group('做法',[('干',0),('汤',0)]))
    item=add(noodles,name,price(row),[f'食品!{row}',f'食品!{row+1}'],gs)
    for n,r in [('面',row),('生面',row+1)]:
        opts=[n]+(['干'] if len(gs)>1 else [])
        check(item,f'食品!L{r}',price(r),*opts)
soup=add(noodles,'鱼丸汤粉',price(44),['食品!44','食品!45','食品!46'],[
    group('面类',[('河粉',0),('米粉',0),('面',0)]),group('配料',[(n,price(r)-price(44)) for n,r in [('鸡丝',44),('鸡丝虾',45),('叉烧',46)]])])
for meat,row in [('鸡丝',44),('鸡丝虾',45),('叉烧',46)]:check(soup,f'食品!L{row}',price(row),'河粉',meat)
white=add(noodles,'清汤白斩鸡腿面',price(75),['食品!75','食品!76'],[group('面类',[('河粉',0),('生面',price(76)-price(75))])])
check(white,'食品!L75',price(75),'河粉');check(white,'食品!L76',price(76),'生面')

rice=category('饭类')
for name,row in [('咖喱鸡饭',59),('卤花肉蛋饭+菜',61),('咖喱鸡椰浆饭',62),('鸡扒炒饭+荷包蛋',63),('黑胡椒汁鸡扒饭',64),('白斩鸡腿+饭+菜',77),('炸鸡腿椰浆饭+煎蛋',78)]:
    item=add(rice,name,price(row),[f'食品!{row}']);check(item,f'食品!L{row}',price(row))
fried=add(rice,'炒饭',price(65),[f'食品!{r}' for r in range(65,71)],[
    group('配料',[('鸡肉',0),('叉烧',price(67)-price(65)),('蔬菜',price(67)-price(65)),('烧肉',price(69)-price(65)),('虾',price(69)-price(65))]),
    group('荷包蛋',[('不加蛋',0),('加荷包蛋',price(66)-price(65))])])
for meat,row in [('鸡肉',65),('叉烧',67),('蔬菜',67),('烧肉',69),('虾',69)]:
    check(fried,f'食品!L{row}',price(row),meat,'不加蛋');check(fried,f'食品!L{row+1}',price(row+1),meat,'加荷包蛋')
stir=category('炒粉面')
friedmee=add(stir,'炒面',price(71),['食品!69','食品!70','食品!71','食品!72'],[
    group('面类',[('面',0),('河粉',0),('米粉',0)]),
    group('配料',[('鸡肉',0),('叉烧',price(72)-price(71)),('烧肉',price(69)-price(71)),('虾',price(69)-price(71))]),
    group('荷包蛋',[('不加蛋',0),('加荷包蛋',price(70)-price(69))])])
for meat,row in [('鸡肉',71),('叉烧',72),('烧肉',69),('虾',69)]:check(friedmee,f'食品!L{row}',price(row),'面',meat,'不加蛋')
for meat in ['烧肉','虾']:check(friedmee,'食品!L70',price(70),'面',meat,'加荷包蛋')

sides=category('小吃与汤类')
feet=add(sides,'冬菇凤爪',6,['食品!37'],[group('份量',[('3份',0),('4份',2)])]);check(feet,'食品!C37',6,'3份');check(feet,'食品!C37',8,'4份')
item=add(sides,'卤花肉加蛋',price(60),['食品!60']);check(item,'食品!L60',price(60))
for name,row,gs in [('云吞',51,[group('做法',[('炸',0),('汤',0)])]),('招牌豆腐（8粒）',52,[]),('虾水饺（4粒）',52,[group('做法',[('炸',0),('汤',0)])]),('葱油蔬菜',54,[group('菜类',[('小白菜',0),('豆芽',0),('菜心',0)])]),('鱼丸汤',55,[]),('煎荷包蛋',56,[]),('鸡排／白斩鸡／炸鸡腿',79,[group('肉类',[('炸鸡排肉',0),('白斩鸡',0),('炸鸡腿',0)])])]:
    item=add(sides,name,price(row),[f'食品!{row}'],gs);check(item,f'食品!L{row}',price(row),*[g['Options'][0]['Name'] for g in gs])
chicken=add(sides,'咖喱鸡肉',6,['食品!53'],[group('份量',[('小份',0),('大份',6)])]);check(chicken,'食品!C53',6,'小份');check(chicken,'食品!L53',12,'大份')
extras=category('加料')
for name,p,ref in [('叉烧',4,'食品!C47'),('咖喱鸡',4,'食品!C47'),('卤花肉',5,'食品!L48'),('虾',5,'食品!L48'),('烧肉',8,'食品!I49'),('加面（大份）',3,'食品!C49')]:
    item=add(extras,name,p,[ref]);check(item,ref,p)
item=add(extras,'加生面',3,['食品!50'],[group('份量',[('普通份',0),('大份',2)])]);check(item,'食品!C50',3,'普通份');check(item,'食品!C50',5,'大份')
breakfast=category('早点')
item=add(breakfast,'烧面包',2.5,['饮料!40','饮料!41'],[group('份量',[('半份',0),('一份',2.5)])]);check(item,'饮料!E41',2.5,'半份');check(item,'饮料!E40',5,'一份')
item=add(breakfast,'甘榜鸡蛋',2,['饮料!40','饮料!41'],[group('数量',[('1粒',0),('2粒',1.5)])]);check(item,'饮料!J41',2,'1粒');check(item,'饮料!J40',3.5,'2粒')

coffee=category('Coffee',True);tea=category('Tea',True);malt=category('Malt & Mixed Drinks',True);soft=category('Soft Drinks',True);juice=category('Fruit Juice',True);beer=category('Beer',True)
formats={'F':'Hot','G':'Glass','H':'Cold','I':'Big','J':'Take away'}
names={3:['Kopi'],4:['Kopi-O'],5:['Kopi-O Kosong'],6:['Teh'],7:['Teh-O'],8:['Teh-C','Kopi-C'],9:['Teh-O Kosong'],10:['Teh-O Lemon'],11:['Kopi Teh Mix'],12:['Ais Lemon Tea'],13:['Lemon'],14:['Milo'],15:['Milo-O'],16:['Milo C Kosong'],17:['Milo Dinosaur Ais'],18:['Nescafe'],19:['Nescafe-O'],20:['Nescafe C Kosong'],21:['White Coffee'],22:['Neslo'],23:['Green Tea'],24:['Green Tea Lemon'],25:['Honey Lemon'],26:['Canned Soft Drinks'],27:['Winter Melon Barli'],28:['Winter Melon Barli Lemon'],29:['Chinese Tea'],30:['Plain Water'],32:['Jia Duo Bao Tea'],34:['Lime Juice with Jelly'],35:['Fruit Juice'],36:['Carrot Milk'],37:['Tiger Beer'],38:['Carlsberg Beer'],39:['Guinness Stout Beer']}
for row,items in names.items():
    vals=[]
    for col,label in formats.items():
        value=drinks[row].get(f'{col}{row}','')
        if re.fullmatch(r'\d+(\.\d+)?',value):
            if row in (32,37,38,39) and col=='H':label='Can'
            if row==26 and col=='G':label='Can'
            vals.append((label,Decimal(value),f'饮料!{col}{row}'))
    assert vals,(row,items)
    base=min(p for _,p,_ in vals)
    for name in items:
        cat=beer if row>=37 else juice if row>=34 else coffee if row in (3,4,5,18,19,20,21) or name=='Kopi-C' else tea if row in (6,7,8,9,10,12,13,23,24,25,29) else malt if row in (11,14,15,16,17,22) else soft
        gs=[]
        if row==26:gs.append(group('Flavour',[('100 Plus',0),('Coke',0)]))
        if row==35:gs.append(group('Fruit',[('Orange',0),('Apple',0),('Carrot',0)]))
        gs.append(group('Serving',[(label,p-base) for label,p,_ in vals]))
        item=add(cat,name,base,[f'饮料!{row}'],gs)
        for label,p,ref in vals:
            for flavour in (['100 Plus','Coke'] if row==26 else ['Orange','Apple','Carrot'] if row==35 else [None]):
                check(item,ref,p,*(([flavour] if flavour else [])+[label]))
mineral=add(soft,'Mineral Water',1.5,['饮料!31'],[group('Size',[('Small',0),('Big',1.5)])]);check(mineral,'饮料!F31',1.5,'Small');check(mineral,'饮料!F31',3,'Big')

prawn['Groups'][1]['Options'][0]['IsDefault'] = True

for cat in categories:
    for item in cat['Items']:
        assert item['Price']>0
        if not cat['IsBeverage']:
            assert not re.search('[a-zA-Z]',item['Name']),item['Name']
        else: assert not re.search('[\u4e00-\u9fff]',item['Name']),item['Name']
        assert all(o['AdditionalPrice']>=0 for g in item['Groups'] for o in g['Options'])
lookup={i['Name']:i for c in categories for i in c['Items']}
for case in checks:
    item=lookup[case['Item']]
    assert len(case['Options'])==len(item['Groups'])
    total=Decimal(str(item['Price']))
    for g,name in zip(item['Groups'],case['Options']):
        total+=Decimal(str(next(o['AdditionalPrice'] for o in g['Options'] if o['Name']==name)))
    assert total==Decimal(str(case['Price'])),case
manifest=dict(Version=args.version,SourceSha256=hashlib.sha256(workbook_bytes).hexdigest(),Categories=categories,PriceChecks=checks)
target=Path(__file__).resolve().parents[1] / 'src/RestaurantPOS.Infrastructure/Catalog/book1-menu.json';target.parent.mkdir(parents=True,exist_ok=True)
target.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(categories=len(categories),items=sum(len(c['Items']) for c in categories),food=sum(len(c['Items']) for c in categories if not c['IsBeverage']),drinks=sum(len(c['Items']) for c in categories if c['IsBeverage']),priceChecks=len(checks)),ensure_ascii=True))
