// Position units per second come from the editable PLC-side Motion.Speed tag.
const target = plc.get('Motion.CommandPosition');
const actual = plc.get('Motion.ActualPosition');
const speed = Math.abs(plc.get('Motion.Speed'));
const distance = target - actual;
const next = actual + Math.sign(distance) * Math.min(Math.abs(distance), speed * dt);
plc.set('Motion.ActualPosition', next);
plc.set('Motion.InPosition', Math.abs(target - next) <= 0.01);
if (plc.changed('Motion.CommandPosition')) plc.log('New position command: ' + target);
state.ticks = (state.ticks || 0) + 1;

// Optional DINT handshake when the triggered version of this contract is loaded.
if (plc.has('RequestSequence') && plc.get('RequestSequence') === plc.get('HandledSequence')) {
  const request = plc.get('RequestSequence');
  plc.set('RequestSequence', request >= 200000000 ? 1 : request + 1);
}
