Exposition des fichiers jeux
.\emulationstation\.emulationstation\themes\.gameinfos\<system>\<rom>.xml
Les systèmes seront dans 
.\emulationstation\.emulationstation\themes\.gameinfos\<system>.xml
Exemple de fichier exposé regroupant panels et scores :
<?xml version="1.0" encoding="utf-8"?>
<theme>

<view name="gamecarousel, detailed, grid">
<!-- ########################### Panel ################################# -->
		<text name="button1" extra="true">
			<text>Light punch</text>
			<color>${white}</color>
		</text>
		<text name="button2" extra="true">
			<text>Light kick</text>
			<color>${blue}</color>
		</text>
		<text name="buttonstart" extra="true">
			<text>Start game</text>
			<color>${yellow}</color>
		</text>		
<!-- ########################### Hiscore ################################# -->
		<text name="hiscoreline1" extra="true">
			<text>#1 JOE 25000</text>
		</text>
		<text name="hiscoreline2" extra="true">
			<text>#2 DER 19000</text>
		</text>
		<text name="hiscoreline3" extra="true">
			<text>#3 ITA 18000</text>
		</text>
		<text name="hiscoreline4" extra="true">
			<text>#4 JOE 17000</text>
		</text>
		<text name="hiscoreline5" extra="true">
			<text>#5 BES 16000</text>
		</text>
		<text name="hiscoreline6" extra="true">
			<text>#6 SER 15000</text>
		</text>
		<text name="hiscoreline7" extra="true">
			<text>#7 SHE 14000</text>
		</text>
		<text name="hiscoreline8" extra="true">
			<text>#8 MPH 13000</text>
		</text>
		<text name="hiscoreline9" extra="true">
			<text>#9 OWA 12000</text>
		</text>
		<text name="hiscoreline10" extra="true">
			<text>#10 RDE 11000</text>
		</text>
		<text name="hiscoreline11" extra="true">
			<text>#11 MIL 10000</text>
		</text>
		<text name="hiscoreline12" extra="true">
			<text>#12 SIT 9000</text>
		</text>
		<text name="hiscoreline13" extra="true">
			<text>#13 KAA 8000</text>
		</text>
		<text name="hiscoreline14" extra="true">
			<text>#14 NDT 7000</text>
		</text>
		<text name="hiscoreline15" extra="true">
			<text>#15 HAN 6000</text>
		</text>
		<text name="hiscoreline16" extra="true">
			<text>#16 KYO 5000</text>
		</text>
		<text name="hiscoreline17" extra="true">
			<text>#17 AFP 4150</text>
		</text>
		<text name="hiscoreline18" extra="true">
			<text>#18 UTE 4000</text>
		</text>
		<text name="hiscoreline19" extra="true">
			<text>#19 DHE 3000</text>
		</text>
		<text name="hiscoreline20" extra="true">
			<text>#20 ALY 2000</text>
		</text>
		<text name="hiscoreline21" extra="true">
			<text>#21 SAM 1000</text>
		</text>
	
	</view>
</theme>
Certains panels systems ont des particularités et le choix du panel courant pourra être paramètré dans un es_features lié au système
Par exemple pour neogeo.json dans \dynpanels\systems
8-Button:NEOGEO MVS TYPE 1 - VARIATION s'il est sélectionné dans les options aura donc une incidence sur les boutons exposés
Grilles (à aligner avec la doc et le model actuel) :
Deux boutons :
1 2
Quatre boutons :
3 4
1 2
Six boutons :
3 4 5
1 2 6
Huit boutons :
3 4 5 7
1 2 6 8

Certaines données comme les joysticks sont des variables qui ne peuvent pas intégrer le xml et donc la solution de contournement est de créer des fichiers textes selon le jeu pour que le theme puisse pinger on off la donnée et afficher en conséquence
DEVICE_TYPE_CHOICES = [
    "unknown",
    "joy2wayhorizontal",
    "joy2wayvertical",
    "joy4way",
    "joy8way",
    "double_joystick",
    "double_joystick_4way",
    "double_joystick_8way",
    "double_joystick_2way_vertical",
    "triggerstick",
    "top_fire_joystick",
    "rotary_joystick",
    "mechanical_rotary_joystick",
    "optical_rotary_joystick",
    "trackball",
    "spinner",
    "dial",
    "vertical_dial",
    "paddle",
    "vertical_paddle",
    "wheel",
    "pedal",
    "pedal2",
    "pedal3",
    "analog_stick",
    "yoke",
    "throttle",
    "shifter",
    "turntable",
    "roller",
    "handlebar",
    "gun",
    "lightgun",
    "mahjong_panel",
    "hanafuda_panel",
    "keypad",
    "gambling_panel",
    "poker_panel",
    "slot_panel",
    "misc",
    "only_buttons",
]
donc cela obligera à créer pour exposer le type joy4way :
.gameinfos/<system>/control/joy4way/<rom>.txt 
donc cela obligera à créer pour exposer le type de jeu vertical :
.gameinfos/<system>/game_type/vertical/<rom>.txt
le contenu du fichier n'a pas d'importance, il doit juste être très léger
