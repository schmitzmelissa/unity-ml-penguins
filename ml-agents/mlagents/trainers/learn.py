# # Unity ML-Agents Toolkit
import logging
import argparse

import os
import glob
import shutil
import numpy as np
import json

from typing import Callable, Optional, List, NamedTuple, Dict

import mlagents.trainers
import mlagents_envs
from mlagents import tf_utils
from mlagents.trainers.trainer_controller import TrainerController
from mlagents.trainers.meta_curriculum import MetaCurriculum
from mlagents.trainers.trainer_util import load_config, TrainerFactory
from mlagents.trainers.stats import (
    TensorboardWriter,
    CSVWriter,
    StatsReporter,
    GaugeWriter,
)
from mlagents_envs.environment import UnityEnvironment
from mlagents.trainers.sampler_class import SamplerManager
from mlagents.trainers.exception import SamplerException
from mlagents_envs.base_env import BaseEnv
from mlagents.trainers.subprocess_env_manager import SubprocessEnvManager
from mlagents_envs.side_channel.side_channel import SideChannel
from mlagents_envs.side_channel.engine_configuration_channel import EngineConfig
from mlagents_envs.exception import UnityEnvironmentException
from mlagents_envs.timers import hierarchical_timer, get_timer_tree
from mlagents.logging_util import create_logger


def _create_parser():
    argparser = argparse.ArgumentParser(
        formatter_class=argparse.ArgumentDefaultsHelpFormatter
    )
    argparser.add_argument("trainer_config_path")
    argparser.add_argument(
        "--env", default=None, dest="env_path", help="Name of the Unity executable "
    )
    argparser.add_argument(
        "--curriculum",
        default=None,
        dest="curriculum_config_path",
        help="Curriculum config yaml file for environment",
    )
    argparser.add_argument(
        "--sampler",
        default=None,
        dest="sampler_file_path",
        help="Reset parameter yaml file for environment",
    )
    argparser.add_argument(
        "--keep-checkpoints",
        default=5,
        type=int,
        help="How many model checkpoints to keep",
    )
    argparser.add_argument(
        "--lesson", default=0, type=int, help="Start learning from this lesson"
    )
    argparser.add_argument(
        "--load",
        default=False,
        dest="load_model",
        action="store_true",
        help="Whether to load the model or randomly initialize",
    )
    argparser.add_argument(
        "--run-id",
        default="ppo",
        help="The directory name for model and summary statistics",
    )
    argparser.add_argument(
        "--save-freq", default=50000, type=int, help="Frequency at which to save model"
    )
    argparser.add_argument(
        "--seed", default=-1, type=int, help="Random seed used for training"
    )
    argparser.add_argument(
        "--train",
        default=False,
        dest="train_model",
        action="store_true",
        help="Whether to train model, or only run inference",
    )
    argparser.add_argument(
        "--base-port",
        default=5005,
        type=int,
        help="Base port for environment communication",
    )
    argparser.add_argument(
        "--num-envs",
        default=1,
        type=int,
        help="Number of parallel environments to use for training",
    )
    argparser.add_argument(
        "--docker-target-name",
        default=None,
        dest="docker_target_name",
        help="Docker volume to store training-specific files",
    )
    argparser.add_argument(
        "--no-graphics",
        default=False,
        action="store_true",
        help="Whether to run the environment in no-graphics mode",
    )
    argparser.add_argument(
        "--debug",
        default=False,
        action="store_true",
        help="Whether to run ML-Agents in debug mode with detailed logging",
    )
    argparser.add_argument(
        "--env-args",
        default=None,
        nargs=argparse.REMAINDER,
        help="Arguments passed to the Unity executable.",
    )
    argparser.add_argument(
        "--cpu", default=False, action="store_true", help="Run with CPU only"
    )

    argparser.add_argument("--version", action="version", version="")

    eng_conf = argparser.add_argument_group(title="Engine Configuration")
    eng_conf.add_argument(
        "--width",
        default=84,
        type=int,
        help="The width of the executable window of the environment(s)",
    )
    eng_conf.add_argument(
        "--height",
        default=84,
        type=int,
        help="The height of the executable window of the environment(s)",
    )
    eng_conf.add_argument(
        "--quality-level",
        default=5,
        type=int,
        help="The quality level of the environment(s)",
    )
    eng_conf.add_argument(
        "--time-scale",
        default=20,
        type=float,
        help="The time scale of the Unity environment(s)",
    )
    eng_conf.add_argument(
        "--target-frame-rate",
        default=-1,
        type=int,
        help="The target frame rate of the Unity environment(s)",
    )
    return argparser


parser = _create_parser()


class RunOptions(NamedTuple):
    trainer_config: Dict
    debug: bool = parser.get_default("debug")
    seed: int = parser.get_default("seed")
    env_path: Optional[str] = parser.get_default("env_path")
    run_id: str = parser.get_default("run_id")
    load_model: bool = parser.get_default("load_model")
    train_model: bool = parser.get_default("train_model")
    save_freq: int = parser.get_default("save_freq")
    keep_checkpoints: int = parser.get_default("keep_checkpoints")
    base_port: int = parser.get_default("base_port")
    num_envs: int = parser.get_default("num_envs")
    curriculum_config: Optional[Dict] = None
    lesson: int = parser.get_default("lesson")
    no_graphics: bool = parser.get_default("no_graphics")
    multi_gpu: bool = parser.get_default("multi_gpu")
    sampler_config: Optional[Dict] = None
    docker_target_name: Optional[str] = parser.get_default("docker_target_name")
    env_args: Optional[List[str]] = parser.get_default("env_args")
    cpu: bool = parser.get_default("cpu")
    width: int = parser.get_default("width")
    height: int = parser.get_default("height")
    quality_level: int = parser.get_default("quality_level")
    time_scale: float = parser.get_default("time_scale")
    target_frame_rate: int = parser.get_default("target_frame_rate")

    @staticmethod
    def from_argparse(args: argparse.Namespace) -> "RunOptions":
        """
        Takes an argparse.Namespace as specified in `parse_command_line`, loads input configuration files
        from file paths, and converts to a CommandLineOptions instance.
        :param args: collection of command-line parameters passed to mlagents-learn
        :return: CommandLineOptions representing the passed in arguments, with trainer config, curriculum and sampler
          configs loaded from files.
        """
        argparse_args = vars(args)
        docker_target_name = argparse_args["docker_target_name"]
        trainer_config_path = argparse_args["trainer_config_path"]
        curriculum_config_path = argparse_args["curriculum_config_path"]
        if docker_target_name is not None:
            trainer_config_path = f"/{docker_target_name}/{trainer_config_path}"
            if curriculum_config_path is not None:
                curriculum_config_path = (
                    f"/{docker_target_name}/{curriculum_config_path}"
                )
        argparse_args["trainer_config"] = load_config(trainer_config_path)
        if curriculum_config_path is not None:
            argparse_args["curriculum_config"] = load_config(curriculum_config_path)
        if argparse_args["sampler_file_path"] is not None:
            argparse_args["sampler_config"] = load_config(
                argparse_args["sampler_file_path"]
            )

        # Since argparse accepts file paths in the config options which don't exist in CommandLineOptions,
        # these keys will need to be deleted to use the **/splat operator below.
        argparse_args.pop("sampler_file_path")
        argparse_args.pop("curriculum_config_path")
        argparse_args.pop("trainer_config_path")
        return RunOptions(**vars(args))


def get_version_string() -> str:
    # pylint: disable=no-member
    return f""" Version information:
  ml-agents: {mlagents.trainers.__version__},
  ml-agents-envs: {mlagents_envs.__version__},
  Communicator API: {UnityEnvironment.API_VERSION},
  TensorFlow: {tf_utils.tf.__version__}"""


def parse_command_line(argv: Optional[List[str]] = None) -> RunOptions:
    args = parser.parse_args(argv)
    return RunOptions.from_argparse(args)


def run_training(run_seed: int, options: RunOptions) -> None:
    """
    Launches training session.
    :param options: parsed command line arguments
    :param run_seed: Random seed used for training.
    :param run_options: Command line arguments for training.
    """
    with hierarchical_timer("run_training.setup"):
        # Recognize and use docker volume if one is passed as an argument
        if not options.docker_target_name:
            model_path = f"./models/{options.run_id}"
            summaries_dir = "./summaries"
        else:
            model_path = f"/{options.docker_target_name}/models/{options.run_id}"
            summaries_dir = f"/{options.docker_target_name}/summaries"
        port = options.base_port

        # Configure CSV, Tensorboard Writers and StatsReporter
        # We assume reward and episode length are needed in the CSV.
        csv_writer = CSVWriter(
            summaries_dir,
            required_fields=[
                "Environment/Cumulative Reward",
                "Environment/Episode Length",
            ],
        )
        tb_writer = TensorboardWriter(summaries_dir)
        gauge_write = GaugeWriter()
        StatsReporter.add_writer(tb_writer)
        StatsReporter.add_writer(csv_writer)
        StatsReporter.add_writer(gauge_write)

        if options.env_path is None:
            port = UnityEnvironment.DEFAULT_EDITOR_PORT
        env_factory = create_environment_factory(
            options.env_path,
            options.docker_target_name,
            options.no_graphics,
            run_seed,
            port,
            options.env_args,
        )
        engine_config = EngineConfig(
            options.width,
            options.height,
            options.quality_level,
            options.time_scale,
            options.target_frame_rate,
        )
        env_manager = SubprocessEnvManager(env_factory, engine_config, options.num_envs)
        maybe_meta_curriculum = try_create_meta_curriculum(
            options.curriculum_config, env_manager, options.lesson
        )
        sampler_manager, resampling_interval = create_sampler_manager(
            options.sampler_config, run_seed
        )
        trainer_factory = TrainerFactory(
            options.trainer_config,
            summaries_dir,
            options.run_id,
            model_path,
            options.keep_checkpoints,
            options.train_model,
            options.load_model,
            run_seed,
            maybe_meta_curriculum,
            options.multi_gpu,
        )
        # Create controller and begin training.
        tc = TrainerController(
            trainer_factory,
            model_path,
            summaries_dir,
            options.run_id,
            options.save_freq,
            maybe_meta_curriculum,
            options.train_model,
            run_seed,
            sampler_manager,
            resampling_interval,
        )

    # Begin training
    try:
        tc.start_learning(env_manager)
    finally:
        env_manager.close()
        write_timing_tree(summaries_dir, options.run_id)


def write_timing_tree(summaries_dir: str, run_id: str) -> None:
    timing_path = f"{summaries_dir}/{run_id}_timers.json"
    try:
        with open(timing_path, "w") as f:
            json.dump(get_timer_tree(), f, indent=4)
    except FileNotFoundError:
        logging.warning(
            f"Unable to save to {timing_path}. Make sure the directory exists"
        )


def create_sampler_manager(sampler_config, run_seed=None):
    resample_interval = None
    if sampler_config is not None:
        if "resampling-interval" in sampler_config:
            # Filter arguments that do not exist in the environment
            resample_interval = sampler_config.pop("resampling-interval")
            if (resample_interval <= 0) or (not isinstance(resample_interval, int)):
                raise SamplerException(
                    "Specified resampling-interval is not valid. Please provide"
                    " a positive integer value for resampling-interval"
                )

        else:
            raise SamplerException(
                "Resampling interval was not specified in the sampler file."
                " Please specify it with the 'resampling-interval' key in the sampler config file."
            )

    sampler_manager = SamplerManager(sampler_config, run_seed)
    return sampler_manager, resample_interval


def try_create_meta_curriculum(
    curriculum_config: Optional[Dict], env: SubprocessEnvManager, lesson: int
) -> Optional[MetaCurriculum]:
    if curriculum_config is None:
        return None
    else:
        meta_curriculum = MetaCurriculum(curriculum_config)
        # TODO: Should be able to start learning at different lesson numbers
        # for each curriculum.
        meta_curriculum.set_all_curricula_to_lesson_num(lesson)
        return meta_curriculum


def prepare_for_docker_run(docker_target_name, env_path):
    for f in glob.glob(
        "/{docker_target_name}/*".format(docker_target_name=docker_target_name)
    ):
        if env_path in f:
            try:
                b = os.path.basename(f)
                if os.path.isdir(f):
                    shutil.copytree(f, "/ml-agents/{b}".format(b=b))
                else:
                    src_f = "/{docker_target_name}/{b}".format(
                        docker_target_name=docker_target_name, b=b
                    )
                    dst_f = "/ml-agents/{b}".format(b=b)
                    shutil.copyfile(src_f, dst_f)
                    os.chmod(dst_f, 0o775)  # Make executable
            except Exception as e:
                logging.getLogger("mlagents.trainers").info(e)
    env_path = "/ml-agents/{env_path}".format(env_path=env_path)
    return env_path


def create_environment_factory(
    env_path: Optional[str],
    docker_target_name: Optional[str],
    no_graphics: bool,
    seed: Optional[int],
    start_port: int,
    env_args: Optional[List[str]],
) -> Callable[[int, List[SideChannel]], BaseEnv]:
    if env_path is not None:
        launch_string = UnityEnvironment.validate_environment_path(env_path)
        if launch_string is None:
            raise UnityEnvironmentException(
                f"Couldn't launch the {env_path} environment. Provided filename does not match any environments."
            )
    docker_training = docker_target_name is not None
    if docker_training and env_path is not None:
        #     Comments for future maintenance:
        #         Some OS/VM instances (e.g. COS GCP Image) mount filesystems
        #         with COS flag which prevents execution of the Unity scene,
        #         to get around this, we will copy the executable into the
        #         container.
        # Navigate in docker path and find env_path and copy it.
        env_path = prepare_for_docker_run(docker_target_name, env_path)
    seed_count = 10000
    seed_pool = [np.random.randint(0, seed_count) for _ in range(seed_count)]

    def create_unity_environment(
        worker_id: int, side_channels: List[SideChannel]
    ) -> UnityEnvironment:
        env_seed = seed
        if not env_seed:
            env_seed = seed_pool[worker_id % len(seed_pool)]
        return UnityEnvironment(
            file_name=env_path,
            worker_id=worker_id,
            seed=env_seed,
            docker_training=docker_training,
            no_graphics=no_graphics,
            base_port=start_port,
            args=env_args,
            side_channels=side_channels,
        )

    return create_unity_environment


def run_cli(options: RunOptions) -> None:
    try:
        print(
            """

                        ▄▄▄▓▓▓▓
                   ╓▓▓▓▓▓▓█▓▓▓▓▓
              ,▄▄▄m▀▀▀'  ,▓▓▓▀▓▓▄                           ▓▓▓  ▓▓▌
            ▄▓▓▓▀'      ▄▓▓▀  ▓▓▓      ▄▄     ▄▄ ,▄▄ ▄▄▄▄   ,▄▄ ▄▓▓▌▄ ▄▄▄    ,▄▄
          ▄▓▓▓▀        ▄▓▓▀   ▐▓▓▌     ▓▓▌   ▐▓▓ ▐▓▓▓▀▀▀▓▓▌ ▓▓▓ ▀▓▓▌▀ ^▓▓▌  ╒▓▓▌
        ▄▓▓▓▓▓▄▄▄▄▄▄▄▄▓▓▓      ▓▀      ▓▓▌   ▐▓▓ ▐▓▓    ▓▓▓ ▓▓▓  ▓▓▌   ▐▓▓▄ ▓▓▌
        ▀▓▓▓▓▀▀▀▀▀▀▀▀▀▀▓▓▄     ▓▓      ▓▓▌   ▐▓▓ ▐▓▓    ▓▓▓ ▓▓▓  ▓▓▌    ▐▓▓▐▓▓
          ^█▓▓▓        ▀▓▓▄   ▐▓▓▌     ▓▓▓▓▄▓▓▓▓ ▐▓▓    ▓▓▓ ▓▓▓  ▓▓▓▄    ▓▓▓▓`
            '▀▓▓▓▄      ^▓▓▓  ▓▓▓       └▀▀▀▀ ▀▀ ^▀▀    `▀▀ `▀▀   '▀▀    ▐▓▓▌
               ▀▀▀▀▓▄▄▄   ▓▓▓▓▓▓,                                      ▓▓▓▓▀
                   `▀█▓▓▓▓▓▓▓▓▓▌
                        ¬`▀▀▀█▓

        """
        )
    except Exception:
        print("\n\n\tUnity Technologies\n")
    print(get_version_string())

    if options.debug:
        log_level = logging.DEBUG
    else:
        log_level = logging.INFO
        # disable noisy warnings from tensorflow
        tf_utils.set_warnings_enabled(False)

    trainer_logger = create_logger("mlagents.trainers", log_level)

    trainer_logger.debug("Configuration for this run:")
    trainer_logger.debug(json.dumps(options._asdict(), indent=4))

    run_seed = options.seed
    if options.cpu:
        os.environ["CUDA_VISIBLE_DEVICES"] = "-1"

    if options.seed == -1:
        run_seed = np.random.randint(0, 10000)
    run_training(run_seed, options)


def main():
    run_cli(parse_command_line())


# For python debugger to directly run this script
if __name__ == "__main__":
    main()
