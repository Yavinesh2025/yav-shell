export function calc(net, rate) {
  return Math.round(net * (1 + rate) * 100) / 100;
}
