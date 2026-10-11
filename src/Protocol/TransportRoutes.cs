using System;
using System.Collections.Generic;
namespace Planet.Protocol {
 public sealed class TransportRoute {
  public string Id,Source,Target,Vehicle;
  public TransportRoute(string source,string target,string vehicle){Source=source;Target=target;Vehicle=vehicle;Id=source+"_"+target;}
 }
 public static class TransportRoutes {
  public static readonly string[] Maps={"meadow","forest","hill","garden","coast"};
  public static string Planet(string region)=>region=="coast"?"海":"草";
  public static string PlanetName(string region)=>region=="coast"?"해안 행성":"초원 행성";
  public static float StationX(string vehicle)=>vehicle=="airship"?-6.2f:-3.2f;
  public const float StationZ=-2.2f;
  public static IEnumerable<TransportRoute> From(string region,string vehicle){
   foreach(var target in Maps)if(target!=region&&((vehicle=="balloon"&&Planet(target)==Planet(region))||(vehicle=="airship"&&Planet(target)!=Planet(region)&&(target=="meadow"||target=="coast"))))yield return new TransportRoute(region,target,vehicle);
  }
  public static TransportRoute Find(string source,string id){foreach(var vehicle in new[]{"airship","balloon"})foreach(var route in From(source,vehicle))if(route.Id==id)return route;return null;}
  public static bool Near(string vehicle,float x,float z){float dx=x-StationX(vehicle),dz=z-StationZ;return dx*dx+dz*dz<=6.25f;}
 }
}
