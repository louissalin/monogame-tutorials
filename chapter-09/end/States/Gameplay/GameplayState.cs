﻿using chapter_09.Engine.Input;
using chapter_09.Engine.Objects;
using chapter_09.Engine.States;
using chapter_09.Input;
using chapter_09.Objects;
using chapter_09.States.Gameplay;
using chapter_09.States.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace chapter_09.States
{
    public class GameplayState : BaseGameState
    {
        private const string BackgroundTexture = "Barren";
        private const string PlayerFighter = "fighter";
        private const string BulletTexture = "bullet";
        private const string ExhaustTexture = "Cloud";
        private const string MissileTexture = "Missile";
        private const string ChopperTexture = "Chopper";
        private const string ExplosionTexture = "explosion";

        private const int MaxExplosionAge = 600; // 10 seconds
        private const int ExplosionActiveLength = 75; // emit particles for 1.2 seconds and let them fade out for 10 seconds

        private Texture2D _missileTexture;
        private Texture2D _exhaustTexture;
        private Texture2D _bulletTexture;
        private Texture2D _explosionTexture;

        private PlayerSprite _playerSprite;

        private bool _isShootingBullets;
        private bool _isShootingMissile;
        private TimeSpan _lastBulletShotAt;
        private TimeSpan _lastMissileShotAt;

        private List<BulletSprite> _bulletList;
        private List<MissileSprite> _missileList;
        private List<ExplosionEmitter> _explosionList;
        private List<ChopperSprite> _enemyList;

        public override void LoadContent()
        {
            _missileTexture = LoadTexture(MissileTexture);
            _exhaustTexture = LoadTexture(ExhaustTexture);
            _bulletTexture = LoadTexture(BulletTexture);
            _explosionTexture = LoadTexture(ExplosionTexture);

            _playerSprite = new PlayerSprite(LoadTexture(PlayerFighter));
            _bulletList = new List<BulletSprite>();
            _missileList = new List<MissileSprite>();
            _explosionList = new List<ExplosionEmitter>();
            _enemyList = new List<ChopperSprite>();

            AddGameObject(new TerrainBackground(LoadTexture(BackgroundTexture)));
            AddGameObject(_playerSprite);

            // position the player in the middle of the screen, at the bottom, leaving a slight gap at the bottom
            var playerXPos = _viewportWidth / 2 - _playerSprite.Width / 2;
            var playerYPos = _viewportHeight - _playerSprite.Height - 30;
            _playerSprite.Position = new Vector2(playerXPos, playerYPos);

            // test chopper sprite
            var chopperSprite = new ChopperSprite(LoadTexture(ChopperTexture));
            chopperSprite.Position = new Vector2(300, 100);
            chopperSprite.OnObjectChanged += _chopperSprite_OnObjectChanged;
            _enemyList.Add(chopperSprite);
            AddGameObject(chopperSprite);

            // load sound effects and register in the sound manager
            var bulletSound = LoadSound("bulletSound");
            var missileSound = LoadSound("missileSound");
            _soundManager.RegisterSound(new GameplayEvents.PlayerShootsBullets(), bulletSound);
            _soundManager.RegisterSound(new GameplayEvents.PlayerShootsMissile(), missileSound, 0.4f, -0.2f, 0.0f);

            // load soundtracks into sound manager
            var track1 = LoadSound("FutureAmbient_1").CreateInstance();
            var track2 = LoadSound("FutureAmbient_2").CreateInstance();
            _soundManager.SetSoundtrack(new List<SoundEffectInstance>() { track1, track2 });
        }

        public override void HandleInput(GameTime gameTime)
        {
            InputManager.GetCommands(cmd =>
            {
                if (cmd is GameplayInputCommand.GameExit)
                {
                    NotifyEvent(new BaseGameStateEvent.GameQuit());
                }

                if (cmd is GameplayInputCommand.PlayerMoveLeft)
                {
                    _playerSprite.MoveLeft();
                    KeepPlayerInBounds();
                }

                if (cmd is GameplayInputCommand.PlayerMoveRight)
                {
                    _playerSprite.MoveRight();
                    KeepPlayerInBounds();
                }

                if (cmd is GameplayInputCommand.PlayerShoots)
                {
                    Shoot(gameTime);
                }
            });
        }

        public override void UpdateGameState(GameTime gameTime)
        {
            foreach (var bullet in _bulletList)
            {
                bullet.MoveUp();
            }

            foreach (var missile in _missileList)
            {
                missile.Update(gameTime);
            }

            foreach (var chopper in _enemyList)
            {
                chopper.Update();
            }

            UpdateExplosions(gameTime);

            // can't shoot bullets more than every 0.2 second
            if (_lastBulletShotAt != null && gameTime.TotalGameTime - _lastBulletShotAt > TimeSpan.FromSeconds(0.2))
            {
                _isShootingBullets = false;
            }

            // can't shoot missiles more than every 1 second
            if (_lastMissileShotAt != null && gameTime.TotalGameTime - _lastMissileShotAt > TimeSpan.FromSeconds(1.0))
            {
                _isShootingMissile = false;
            }

            // check for bullet collisions
            var bulletCollisionDectector = new AABBCollisionDetector<BulletSprite, ChopperSprite>(_bulletList);
            var missileCollisionDectector = new AABBCollisionDetector<MissileSprite, ChopperSprite>(_missileList);

            bulletCollisionDectector.DetectCollisions(_enemyList, (bullet, chopper) =>
            {
                var hitEvent = new GameplayEvents.BulletHitsChopper();
                chopper.OnNotify(hitEvent);
                _soundManager.OnNotify(hitEvent);
                bullet.Destroy();
            });

            missileCollisionDectector.DetectCollisions(_enemyList, (missile, chopper) =>
            {
                var hitEvent = new GameplayEvents.MissileHitsChopper();
                chopper.OnNotify(hitEvent);
                _soundManager.OnNotify(hitEvent);
                missile.Destroy();
            });

            // get rid of bullets and missiles that have gone out of view
            _bulletList = CleanObjects(_bulletList);
            _missileList = CleanObjects(_missileList);
        }

        private List<T> CleanObjects<T>(List<T> objectList) where T : BaseGameObject
        {
            List<T> listOfItemsToKeep = new List<T>();
            foreach(T item in objectList)
            {
                var offScreen = item.Position.Y < -50;

                if (offScreen || item.Destroyed)
                {
                    RemoveGameObject(item);
                }
                else
                {
                    listOfItemsToKeep.Add(item);
                }
            }

            return listOfItemsToKeep;
        }

        private async void _chopperSprite_OnObjectChanged(object sender, BaseGameStateEvent e)
        {
            var chopper = (ChopperSprite)sender;
            switch (e)
            {
                case GameplayEvents.EnemyLostLife ge:
                    if (ge.CurrentLife <= 0)
                    {
                        AddExplosion(new Vector2(chopper.Position.X - 40, chopper.Position.Y - 40));
                        await Task.Delay(TimeSpan.FromSeconds(0.5));
                        RemoveGameObject(chopper);
                        _enemyList.Remove(chopper);
                    }
                    break;
            }
        }

        private void AddExplosion(Vector2 position)
        {
            var explosion = new ExplosionEmitter(_explosionTexture, position);
            AddGameObject(explosion);
            _explosionList.Add(explosion);
        }

        private void UpdateExplosions(GameTime gameTime)
        {
            foreach (var explosion in _explosionList)
            {
                explosion.Update(gameTime);
                
                if (explosion.Age > ExplosionActiveLength)
                {
                    explosion.Deactivate();
                }

                if (explosion.Age > MaxExplosionAge)
                {
                    RemoveGameObject(explosion);
                }
            }
        }
 
        private void Shoot(GameTime gameTime)
        {
            if (!_isShootingBullets)
            {
                CreateBullets();
                _isShootingBullets = true;
                _lastBulletShotAt = gameTime.TotalGameTime;

                NotifyEvent(new GameplayEvents.PlayerShootsBullets());
            }

            if (!_isShootingMissile)
            {
                CreateMissile();
                _isShootingMissile = true;
                _lastMissileShotAt = gameTime.TotalGameTime;

                NotifyEvent(new GameplayEvents.PlayerShootsMissile());
            }
        }

        private void CreateBullets()
        {
            var bulletSpriteLeft = new BulletSprite(_bulletTexture);
            var bulletSpriteRight = new BulletSprite(_bulletTexture);

            var bulletY = _playerSprite.Position.Y + 30;
            var bulletLeftX = _playerSprite.Position.X + _playerSprite.Width / 2 - 40;
            var bulletRightX = _playerSprite.Position.X + _playerSprite.Width / 2 + 10;

            bulletSpriteLeft.Position = new Vector2(bulletLeftX, bulletY);
            bulletSpriteRight.Position = new Vector2(bulletRightX, bulletY);

            _bulletList.Add(bulletSpriteLeft);
            _bulletList.Add(bulletSpriteRight);

            AddGameObject(bulletSpriteLeft);
            AddGameObject(bulletSpriteRight);
        }

        private void CreateMissile()
        {
            var missileSprite = new MissileSprite(_missileTexture, _exhaustTexture);
            missileSprite.Position = new Vector2(_playerSprite.Position.X + 33, _playerSprite.Position.Y - 25);

            _missileList.Add(missileSprite);
            AddGameObject(missileSprite);
        }

        private void KeepPlayerInBounds()
        {
            if (_playerSprite.Position.X < 0)
            {
                _playerSprite.Position = new Vector2(0, _playerSprite.Position.Y);
            }

            if (_playerSprite.Position.X > _viewportWidth - _playerSprite.Width)
            {
                _playerSprite.Position = new Vector2(_viewportWidth - _playerSprite.Width, _playerSprite.Position.Y);
            }

            if (_playerSprite.Position.Y < 0)
            {
                _playerSprite.Position = new Vector2(_playerSprite.Position.X, 0);
            }

            if (_playerSprite.Position.Y > _viewportHeight - _playerSprite.Height)
            {
                _playerSprite.Position = new Vector2(_playerSprite.Position.X, _viewportHeight - _playerSprite.Height);
            }
        }

        protected override void SetInputManager()
        {
            InputManager = new InputManager(new GameplayInputMapper());
        }
    }
}